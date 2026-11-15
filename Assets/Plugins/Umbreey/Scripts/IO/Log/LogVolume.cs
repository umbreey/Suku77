using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Umbreey
{
    /// <summary>
    /// 本地日志卷：一个卷 == 一次会话（术语见 CONTEXT.md「卷」）。
    /// 设计见 specs/0001「本地卷」，排空节奏的取舍见 ADR-0001。
    ///
    /// <para>
    /// **卷边界等于会话边界**：每次启动开一个新卷，卷名
    /// <c>{productName_下划线化}_{yyyy_MM_dd}_{traceId 前 8 位}.log</c>。
    /// 单卷上限（LogConfig.VolumeMaxBytes）与 SessionLimitBytes 对齐，保证一次会话永不跨卷 ——
    /// 这是"发一个卷 == 发完整一次运行"成立的前提。卷名里的 traceId 顺带解决了 PC 多开
    /// 抢同一个文件的问题，**不需要再加 PID 后缀**。
    /// </para>
    ///
    /// <para>
    /// 写入策略：FileStream **常开**，每次写完 Flush()（**不带 true**）。
    /// Flush() 只把托管缓冲推给内核，是一次 syscall、不触发磁盘 IO；Flush(true) 是 fsync，
    /// 我们不需要它 —— 进程崩溃丢的是进程内存，不是 OS 页缓存（ADR-0001）。
    /// 本地卷**不参与攒批**，每次缓冲交换就立即写。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 线程模型：**只由打包线程写**。主线程的 Flush 语义是"通知打包线程 + 等它确认"，
    /// 绝不能自己动手写盘，否则两个线程会同时碰同一个 FileStream（ADR-0001）。
    /// 因此本类**不得调用任何 Unity API**（全局硬约束 1）——
    /// persistentDataPath / productName 等值必须由主线程预取后从构造参数传进来。
    /// 唯一的例外是 <see cref="Close"/>：它可能在打包线程 Join 超时后由主线程兜底调用，
    /// 内部对此做了容错（重复关闭、关闭与写入交错都只会走进 catch，不会抛出去）。
    /// </para>
    ///
    /// <para>
    /// 票 03 落地写卷，票 04 补上**启动时的一次性保留策略**（<see cref="CleanupOnStartup"/>
    /// 与纯函数 <see cref="SelectForDeletion"/>）；生命周期 flush 由票 06 继续填。
    /// </para>
    ///
    /// <para>
    /// **卷满与限流心跳的关系**（specs/0001「与限流的冲突：卷满之后心跳往哪写」，票 05 确认）：
    /// 心跳是 Internal 日志，它 bypass 的是**限流计数**，**不 bypass 卷满**。
    /// 卷满属于硬停 —— <see cref="Write"/> 在 <see cref="IsFull"/> 之后对**任何**记录一律早退，
    /// 包括 Internal 的心跳与标记行，本地卷从 <see cref="FullNotice"/> 那一行之后完全静默；
    /// 但心跳照常进批次累加器、进 outbox（上传侧还有它自己的配额）。
    /// 本类**不需要为此加任何分支**，这段注释是为了让下一个读代码的人知道那不是漏写。
    /// </para>
    /// </summary>
    internal sealed class LogVolume
    {
        /// <summary>卷的扩展名。保留策略（票 04）按它筛文件，改这里要同步改那边。</summary>
        internal const string Extension = ".log";

        /// <summary>卷名里的日期格式。天数判定要从卷名反解析它（票 04），两处必须一致。</summary>
        internal const string DateFormat = "yyyy_MM_dd";

        /// <summary>卷名里取 traceId 的前几位。票中人工验收期望的是 8 位十六进制。</summary>
        internal const int TraceIdPrefixLength = 8;

        /// <summary>
        /// **多开豁免**的时间窗：mtime 落在这么久之内的**非己文件**一律跳过清理
        /// （specs/0001「本地卷」：清理跳过 mtime 在 5 分钟内的非己卷）。
        ///
        /// <para>
        /// PC 上开两个客户端对练时，删掉对方正在写的卷会让对方的日志凭空消失。
        /// "最近被写过 = 可能有人正在用"是唯一不需要跨进程锁就能拿到的证据。
        /// </para>
        /// <para>
        /// ⚠️ **这是 mtime 在本类里唯一的用途。** 卷的"年龄"（<see cref="LogConfig.VolumeKeepDays"/>）
        /// 必须从**文件名**解析日期，mtime 会被文件拷贝和系统时间变化污染。两个用途不冲突，
        /// 也不要合并。
        /// </para>
        /// <para>
        /// 它不是可调阈值（和 <see cref="StreamBufferBytes"/> 一样），故不进参数总表：
        /// 5 分钟这个数由 specs/0001 与契约 C3（outbox 清理 .tmp 的同一条规则）共同规定，
        /// 两处必须是同一个数。要改它得先改那两处文档，并把它升格成 LogConfig 字段。
        /// </para>
        /// </summary>
        internal const int ActiveVolumeGraceSec = 5 * 60;

        /// <summary>
        /// 卷满时写下的最后一行。之后本地卷完全静默（specs/0001「与限流的冲突」）——
        /// 没有这一行的话，卷会看起来像是"日志突然断了"，分不清是崩溃还是写满。
        /// </summary>
        internal const string FullNotice = "[LogSystem] volume full, local logging stopped";

        /// <summary>
        /// 行分隔符固定用 '\n'，不用 Environment.NewLine：卷会被 adb pull 到另一个系统上看，
        /// 让卷的字节形态与产出平台无关比迁就某个平台的记事本更重要。
        /// </summary>
        private const byte LineBreak = (byte)'\n';

        /// <summary>
        /// FileStream 自己的缓冲大小。我们每轮都 Flush()，它只影响单次 write 的合并粒度。
        /// </summary>
        private const int StreamBufferBytes = 4096;

        /// <summary>
        /// 编码缓冲的初始大小与"可留存上限"。超过上限的巨型串用一次性数组编码后即弃，
        /// 免得一条爆栈日志把这块缓冲永久撑大。两者都不是可调阈值，故不进参数总表。
        /// </summary>
        private const int EncodeBufferBytes = 4096;

        private const int MaxRetainedEncodeBufferBytes = 64 * 1024;

        /// <summary>日期段的字符数。DateFormat 是 const，但 const 表达式里不能取 .Length。</summary>
        private static readonly int DateTextLength = DateFormat.Length;

        private readonly LogConfig m_Config;
        private readonly string m_Dir;
        private readonly string m_FilePath;

        /// <summary>
        /// 主线程预取的 Application.productName（**原值，含空格**）。
        /// 保留策略要用它认出"哪些文件是本游戏写的"，清洗一律走
        /// <see cref="SanitizeFileNamePart"/> —— 写与读用同一个规则，这正是旧 deleteYesterday
        /// 从未生效的那个坑。
        /// </summary>
        private readonly string m_ProductName;

        /// <summary>启动清理**只做一次**（specs/0001）。重复调用直接返回。</summary>
        private bool m_CleanupDone;

        /// <summary>常开的句柄。null 表示还没 Open 或已经 Close。</summary>
        private FileStream m_Stream;

        /// <summary>本卷已写字节数。Open 时以文件现有长度为起点。</summary>
        private long m_Bytes;

        private bool m_Full;

        /// <summary>
        /// 写盘失败只报一次。没有这个闩，"写盘失败 → 打日志 → 又一次写盘失败"会持续刷屏。
        /// （Internal 日志的通用重入守卫与限频是票 05 的事，这里只做本地的一次性闩。）
        /// </summary>
        private bool m_WriteFailureReported;

        private byte[] m_EncodeBuffer = new byte[EncodeBufferBytes];

        /// <param name="cfg">读取 VolumeMaxBytes / VolumeKeepCount / LocalMaxBytes / VolumeKeepDays。</param>
        /// <param name="dir">日志目录，**主线程预取的** Application.persistentDataPath 派生值。</param>
        /// <param name="productName">**主线程预取的** Application.productName（写入时空格换下划线）。</param>
        /// <param name="traceId">本次会话的追踪标识，取前 8 位进卷名。</param>
        internal LogVolume(LogConfig cfg, string dir, string productName, string traceId)
        {
            m_Config = cfg;
            m_Dir = dir;
            m_ProductName = productName;
            m_FilePath = Path.Combine(dir, BuildFileName(productName, DateTime.Now, traceId));
        }

        /// <summary>
        /// 当前会话卷的完整路径。<see cref="LogSystem.LogPath"/> 透出它，供飞书一键发送使用。
        /// <para>刻意不叫 Path：那会遮蔽 System.IO.Path，让本文件里的 Path.Combine 解析到属性上。</para>
        /// </summary>
        internal string FilePath
        {
            get { return m_FilePath; }
        }

        /// <summary>
        /// 卷是否已达 VolumeMaxBytes。
        /// <para>
        /// 卷满属于**硬停**：写最后一行 <see cref="FullNotice"/>，之后本地卷完全静默，
        /// 限流心跳也不再写卷（但仍进 outbox，上传侧还有自己的配额）。见 specs/0001。
        /// </para>
        /// </summary>
        internal bool IsFull
        {
            get { return m_Full; }
        }

        /// <summary>
        /// 计算卷名。纯函数（时间由调用方传入），因此可在 EditMode 里直接测。
        /// <para>
        /// 日期用**本地时间**：卷名是给人看的，也是保留策略判天数的依据 ——
        /// 这是墙上时钟的三个合法用途之一（全局硬约束 2）。
        /// </para>
        /// <para>
        /// ⚠️ 日期**必须用 InvariantCulture 格式化**：设备的当前区域可能挂着非公历日历
        /// （泰语的佛历、部分阿拉伯语区的回历），那时 <c>ToString("yyyy_MM_dd")</c> 会写出
        /// 2569 这样的年份。保留策略是拿同一个格式**反解析**卷名的，两边用不同的日历就等于
        /// 又一次"写与读规则不一致" —— 结果是所有卷的日期都落在未来、永远不过期、
        /// 无限堆积，正是本次要修掉的那个 bug 换个形状再来一遍。
        /// </para>
        /// </summary>
        /// <param name="productName">Application.productName。含空格（"Niniko GO"）故必须清洗。</param>
        /// <param name="localNow">本地时间。</param>
        /// <param name="traceId">本次会话的追踪标识，取前 <see cref="TraceIdPrefixLength"/> 位。</param>
        internal static string BuildFileName(string productName, DateTime localNow, string traceId)
        {
            string product = SanitizeFileNamePart(productName);
            string trace = SanitizeFileNamePart(traceId);
            if (trace.Length > TraceIdPrefixLength)
            {
                trace = trace.Substring(0, TraceIdPrefixLength);
            }

            return product + "_" + localNow.ToString(DateFormat, CultureInfo.InvariantCulture)
                   + "_" + trace + Extension;
        }

        /// <summary>
        /// 把一段文本清洗成能安全进文件名的形式：空格与所有非法字符一律换成下划线。
        /// <para>
        /// 旧 LogWrite 的 deleteYesterday 从未生效，根因就是**写入时换了下划线、
        /// 删除时又拿原名去匹配**（productName 是 "Niniko GO"，含空格）。
        /// 所以清洗规则必须是一处定义、写与读都用它 —— 票 04 解析卷名时同样引用本方法。
        /// </para>
        /// </summary>
        internal static string SanitizeFileNamePart(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "unknown";
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool bad = c == ' ';
                if (!bad)
                {
                    for (int j = 0; j < invalid.Length; j++)
                    {
                        if (c == invalid[j])
                        {
                            bad = true;
                            break;
                        }
                    }
                }

                sb.Append(bad ? '_' : c);
            }

            return sb.ToString();
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  保留策略（票 04 / specs/0001「本地卷」）
        //
        //  ⚠️ 这是整个日志系统里**删错代价最高**的一段：删错 = 丢日志，而且丢的是玩家设备上
        //  唯一的一份。因此拆成两半：
        //
        //    · SelectForDeletion  —— **纯函数**。输入「文件列表 + 当前时刻 + 配置 + 当前卷名」，
        //                            输出「该删哪些」。不碰文件系统、不碰 Unity API，
        //                            所有分支都能在 EditMode 里直接摆出来。
        //    · CleanupOnStartup   —— 只负责 IO：扫目录、按计划删、失败记一条日志。
        //
        //  时机：**只在启动时做一次**，不在每次写卷时做。因此 LocalMaxBytes 是
        //  「历史卷预算」而**不是运行期硬上限** —— 清理之后当前卷还会继续长到 VolumeMaxBytes，
        //  磁盘峰值 = LocalMaxBytes + VolumeMaxBytes。这是有意选择（避免每次写卷都扫目录），
        //  但不要宣称两者等价。
        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 保留策略的输入项：清理只需要文件的这三个属性。
        /// <para>
        /// 刻意不传 <see cref="FileInfo"/> —— 那会把纯函数拴在真实文件系统上，
        /// 而"删错哪个"这件事必须能在内存里穷举出来。
        /// </para>
        /// </summary>
        internal struct LogVolumeFile
        {
            /// <summary>文件名，**含扩展名、不含目录**。</summary>
            internal readonly string Name;

            /// <summary>文件字节数，用于 <see cref="LogConfig.LocalMaxBytes"/> 的预算。</summary>
            internal readonly long Bytes;

            /// <summary>
            /// 最后写入时间（本地时钟）。⚠️ **只用于多开豁免**
            /// （<see cref="ActiveVolumeGraceSec"/>），**绝不用于判定卷的年龄** ——
            /// 年龄一律从文件名解析。
            /// </summary>
            internal readonly DateTime LastWriteLocal;

            internal LogVolumeFile(string name, long bytes, DateTime lastWriteLocal)
            {
                Name = name;
                Bytes = bytes;
                LastWriteLocal = lastWriteLocal;
            }
        }

        /// <summary>
        /// <see cref="SelectForDeletion"/> 的结果：删哪些、其中多少是旧格式、一共多大。
        /// 后两项只为让启动那条诊断日志说得出"这次清掉了什么"。
        /// </summary>
        internal sealed class LogRetentionPlan
        {
            /// <summary>待删的文件名（含扩展名、不含目录）。旧格式在前，其余按从旧到新排。</summary>
            internal readonly List<string> Delete = new List<string>();

            /// <summary><see cref="Delete"/> 里属于旧格式一次性迁移的条数。</summary>
            internal int LegacyCount;

            /// <summary>预计释放的字节数。</summary>
            internal long Bytes;
        }

        /// <summary>文件名的三种归属。只有前两种会被删，<see cref="Ignore"/> 是绝对安全区。</summary>
        private enum FileKind
        {
            /// <summary>不是本游戏写的日志（别的程序留下的 .log、client_id 文件、乱名文件）。**永不删除。**</summary>
            Ignore,

            /// <summary>新卷：<c>{product}_{yyyy_MM_dd}_{8位十六进制}.log</c>。按三条规则淘汰。</summary>
            Volume,

            /// <summary>旧格式：<c>{product}_{yyyy_MM_dd}[_{alias}].log</c>。一次性迁移删除。</summary>
            Legacy,
        }

        /// <summary>
        /// **纯函数**：算出该删哪些文件。不碰文件系统、不碰 Unity API、不看时钟
        /// （当前时刻由调用方传入），因此可以在 EditMode 里把每个分支摆出来直接断言。
        ///
        /// <para>
        /// 三条规则**谁先到删谁，同时命中取并集、不重复删**：
        /// 卷数量 <see cref="LogConfig.VolumeKeepCount"/>、总字节 <see cref="LogConfig.LocalMaxBytes"/>、
        /// 天数 <see cref="LogConfig.VolumeKeepDays"/>。外加旧格式的一次性迁移删除。
        /// </para>
        ///
        /// <para>
        /// **两条豁免先于一切规则生效**（顺序不能反，反了就会出现"当前卷被当成过期文件删掉"）：
        /// <list type="number">
        /// <item>当前会话的卷（<paramref name="currentFileName"/>）—— 在**任何**规则下都不删。
        /// 玩家把系统时间调到未来时历史卷会集体过期；调到过去时新卷的日期会落在保留期外，
        /// 不豁免就会把**正在写的那个文件**当场删掉。</item>
        /// <item>mtime 在 <see cref="ActiveVolumeGraceSec"/> 之内的非己文件 —— 可能是另一个实例
        /// 正在写（PC 多开对练），删掉会让对方的日志凭空消失。</item>
        /// </list>
        /// 被豁免的文件**整个退出这次清理**，也不计入 KeepCount / LocalMaxBytes 的预算：
        /// 宁可这一次少删一点（下次启动自然会收），也不要为了凑预算去删更新的卷。
        /// </para>
        ///
        /// <para>
        /// 天数**从文件名解析**（<see cref="DateFormat"/>），不用 mtime —— mtime 会被文件拷贝
        /// 和系统时间变化污染。这是墙上时钟的三个合法用途之一（全局硬约束 2）。
        /// </para>
        /// </summary>
        /// <param name="files">目录里的候选文件。null 或空都合法，返回空计划。</param>
        /// <param name="localNow">当前**本地**时刻。卷名用的也是本地时间，两者必须同一套时钟。</param>
        /// <param name="cfg">读取 VolumeKeepCount / LocalMaxBytes / VolumeKeepDays。</param>
        /// <param name="currentFileName">当前会话卷的文件名（不含目录）。空串表示"没有当前卷"。</param>
        /// <param name="productName">Application.productName **原值**，内部自行清洗。</param>
        internal static LogRetentionPlan SelectForDeletion(
            IReadOnlyList<LogVolumeFile> files,
            DateTime localNow,
            LogConfig cfg,
            string currentFileName,
            string productName)
        {
            var plan = new LogRetentionPlan();
            if (files == null || files.Count == 0 || cfg == null)
            {
                return plan;
            }

            // 写文件名时清洗过，读文件名时必须用**同一个**清洗规则 ——
            // 旧 deleteYesterday 从未生效的根因正是这两处不一致（写下划线、读原名）。
            string prefix = SanitizeFileNamePart(productName) + "_";

            var legacy = new List<LogVolumeFile>();
            var volumes = new List<LogVolumeFile>();
            var volumeDates = new List<DateTime>();

            for (int i = 0; i < files.Count; i++)
            {
                LogVolumeFile file = files[i];
                if (string.IsNullOrEmpty(file.Name))
                {
                    continue;
                }

                // 豁免 1：当前会话的卷。放在最前面 —— 它必须先于**所有**规则，
                // 包括"日期落在保留期外"这一条（系统时间被回拨时正是这个情形）。
                // 用 OrdinalIgnoreCase：只可能多保一个文件，不可能多删一个。
                if (string.Equals(file.Name, currentFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DateTime date;
                FileKind kind = Classify(file.Name, prefix, out date);
                if (kind == FileKind.Ignore)
                {
                    continue;
                }

                // 豁免 2：另一个实例可能正在写它。旧格式同样适用 —— 票 11 之前新旧两套并存，
                // 对面那个客户端此刻可能还在往旧格式文件里写。
                if (IsRecentlyWritten(file.LastWriteLocal, localNow))
                {
                    continue;
                }

                if (kind == FileKind.Legacy)
                {
                    legacy.Add(file);
                }
                else
                {
                    volumes.Add(file);
                    volumeDates.Add(date);
                }
            }

            SortOldestFirst(volumes, volumeDates);

            var chosen = new HashSet<string>(StringComparer.Ordinal);

            // 规则 0（本票附带的一次性迁移）：旧格式全删。
            // 它们的命名规则与新卷不同，靠新卷的保留逻辑自然淘汰是做不到的。
            for (int i = 0; i < legacy.Count; i++)
            {
                if (Choose(plan, chosen, legacy[i]))
                {
                    plan.LegacyCount++;
                }
            }

            // 规则 1：天数。从**文件名**解析出的日期算整日差，与 mtime 无关。
            for (int i = 0; i < volumes.Count; i++)
            {
                if (AgeInDays(volumeDates[i], localNow) >= cfg.VolumeKeepDays)
                {
                    Choose(plan, chosen, volumes[i]);
                }
            }

            // 规则 2：数量。留最新的 VolumeKeepCount 个**历史卷**（当前卷已豁免、不占名额）。
            int excess = volumes.Count - (cfg.VolumeKeepCount > 0 ? cfg.VolumeKeepCount : 0);
            for (int i = 0; i < excess; i++)
            {
                Choose(plan, chosen, volumes[i]);
            }

            // 规则 3：总字节。同样只约束历史卷（当前卷还会继续长，见本节顶部的注释）。
            long retained = 0;
            for (int i = 0; i < volumes.Count; i++)
            {
                if (!chosen.Contains(volumes[i].Name))
                {
                    retained += volumes[i].Bytes;
                }
            }

            for (int i = 0; i < volumes.Count && retained > cfg.LocalMaxBytes; i++)
            {
                if (Choose(plan, chosen, volumes[i]))
                {
                    retained -= volumes[i].Bytes;
                }
            }

            return plan;
        }

        /// <summary>
        /// 认出一个文件名是新卷、旧格式、还是"不关我们的事"。
        ///
        /// <para>
        /// 判定链是**逐段收紧**的，任何一段不符就落进 <see cref="FileKind.Ignore"/>：
        /// <list type="number">
        /// <item>扩展名必须是 <see cref="Extension"/></item>
        /// <item>必须以「清洗后的 productName + '_'」开头 —— 这一条挡住别的程序留下的 .log</item>
        /// <item>紧接着必须是一个能按 <see cref="DateFormat"/> 解析出来的日期</item>
        /// <item>日期之后：空 = 旧格式（无 alias）；<c>_{8 位十六进制}</c> = 新卷；
        /// <c>_{其它}</c> = 旧格式（带 alias，旧代码取的是登录名前 10 个字符）；
        /// 不以 <c>_</c> 开头 = 不是我们写的，忽略</item>
        /// </list>
        /// </para>
        /// <para>
        /// ⚠️ 唯一的歧义是"alias 恰好是 8 位十六进制"（实际 alias 固定 10 个字符，撞不上）。
        /// 万一撞了，它会被当成**新卷**——歧义一律倒向"更保守"的那一侧：
        /// 新卷只会按三条规则正常淘汰，绝不会在迁移里被整批删掉。
        /// </para>
        /// </summary>
        private static FileKind Classify(string fileName, string prefix, out DateTime date)
        {
            date = default(DateTime);

            if (!fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            {
                return FileKind.Ignore;
            }

            string stem = fileName.Substring(0, fileName.Length - Extension.Length);
            if (!stem.StartsWith(prefix, StringComparison.Ordinal))
            {
                return FileKind.Ignore;
            }

            if (stem.Length < prefix.Length + DateTextLength)
            {
                return FileKind.Ignore;
            }

            string dateText = stem.Substring(prefix.Length, DateTextLength);
            if (!DateTime.TryParseExact(
                    dateText, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                return FileKind.Ignore;
            }

            string rest = stem.Substring(prefix.Length + DateTextLength);
            if (rest.Length == 0)
            {
                return FileKind.Legacy;
            }

            if (rest[0] != '_')
            {
                // 日期后面直接跟着别的字符（如 "..._2026_09_08x.log"）：新旧两套都写不出这种名字。
                return FileKind.Ignore;
            }

            string suffix = rest.Substring(1);
            return suffix.Length == TraceIdPrefixLength && IsHex(suffix) ? FileKind.Volume : FileKind.Legacy;
        }

        private static bool IsHex(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 卷的年龄，单位整日，**从文件名的日期算**。
        /// <para>
        /// 日期在未来（系统时间被回拨）时返回负数，因此永远不会 ≥ VolumeKeepDays ——
        /// 时钟乱掉时的方向是"少删"，不是"多删"。
        /// </para>
        /// </summary>
        private static int AgeInDays(DateTime fileDate, DateTime localNow)
        {
            return (localNow.Date - fileDate.Date).Days;
        }

        /// <summary>
        /// 多开豁免的判定：mtime 距今在 <see cref="ActiveVolumeGraceSec"/> 之内。
        /// <para>
        /// mtime 落在未来（系统时间被回拨、或文件从别的机器拷来）时一律**当作正在被写**：
        /// 这种文件我们对它一无所知，不删是唯一安全的选择。
        /// </para>
        /// </summary>
        private static bool IsRecentlyWritten(DateTime lastWriteLocal, DateTime localNow)
        {
            return (localNow - lastWriteLocal).TotalSeconds < ActiveVolumeGraceSec;
        }

        /// <summary>
        /// 从旧到新排：先按**文件名里的日期**，同日再按文件名字典序。
        /// <para>
        /// 同日的次序其实无所谓（都是同一天的卷），但排序必须是确定的 ——
        /// 不确定的次序会让"删的是最旧的那几个"这类断言变成偶尔失败的脆弱测试。
        /// 用插入排序：一次启动最多几百个文件，代码短、行为好推。
        /// </para>
        /// </summary>
        private static void SortOldestFirst(List<LogVolumeFile> volumes, List<DateTime> dates)
        {
            for (int i = 1; i < volumes.Count; i++)
            {
                LogVolumeFile file = volumes[i];
                DateTime date = dates[i];

                int j = i - 1;
                while (j >= 0 && IsNewer(volumes[j], dates[j], file, date))
                {
                    volumes[j + 1] = volumes[j];
                    dates[j + 1] = dates[j];
                    j--;
                }

                volumes[j + 1] = file;
                dates[j + 1] = date;
            }
        }

        private static bool IsNewer(LogVolumeFile a, DateTime aDate, LogVolumeFile b, DateTime bDate)
        {
            int byDate = aDate.CompareTo(bDate);
            if (byDate != 0)
            {
                return byDate > 0;
            }

            return string.CompareOrdinal(a.Name, b.Name) > 0;
        }

        /// <summary>
        /// 把一个文件记进删除计划。**同一个文件被多条规则命中时只记一次**
        /// （票里的"取并集、不重复删"）。
        /// </summary>
        /// <returns>true 表示这次真的加进去了（之前没被别的规则选中）。</returns>
        private static bool Choose(LogRetentionPlan plan, HashSet<string> chosen, LogVolumeFile file)
        {
            if (!chosen.Add(file.Name))
            {
                return false;
            }

            plan.Delete.Add(file.Name);
            plan.Bytes += file.Bytes;
            return true;
        }

        /// <summary>
        /// 扫一次日志目录，按 <see cref="SelectForDeletion"/> 的计划删文件。**整个进程只做一次。**
        ///
        /// <para>
        /// ⚠️ 由**打包线程**在进入主循环前调用，不是主线程：玩家设备上积累的旧格式日志可能有
        /// 几百个（deleteYesterday 从未生效，从装机那天起一直在攒），把这次扫描 + 批量删除放进
        /// BeforeSceneLoad 会直接变成启动卡顿。<see cref="Directory"/> / <see cref="File"/>
        /// 都是 BCL，不是 Unity API，后台线程调用安全（全局硬约束 1）。
        /// </para>
        /// <para>
        /// **任何失败都不向外抛、不中断启动**：文件被别的进程占用、目录权限不对、
        /// 属性读不出来 —— 全都只是"这次少删几个"，绝不能让日志系统把宿主的启动搞挂。
        /// 失败按全局硬约束 6 记一条 Internal 日志。
        /// </para>
        /// </summary>
        internal void CleanupOnStartup()
        {
            if (m_CleanupDone)
            {
                return;
            }

            m_CleanupDone = true;

            try
            {
                // 不用 Directory.GetFiles(dir, "*.log")：Windows 的通配符会连带匹配
                // 更长的扩展名（8.3 短名的历史包袱），筛扩展名的事交给 Classify 一处做。
                string[] paths = Directory.GetFiles(m_Dir);
                var files = new List<LogVolumeFile>(paths.Length);
                for (int i = 0; i < paths.Length; i++)
                {
                    try
                    {
                        var info = new FileInfo(paths[i]);
                        if (info.Exists)
                        {
                            files.Add(new LogVolumeFile(info.Name, info.Length, info.LastWriteTime));
                        }
                    }
                    catch (Exception)
                    {
                        // 单个文件的属性读不出来就当它不存在：读不到 = 不敢删，安全的那一侧。
                    }
                }

                LogRetentionPlan plan = SelectForDeletion(
                    files, DateTime.Now, m_Config, Path.GetFileName(m_FilePath), m_ProductName);

                int deleted = 0;
                int failed = 0;
                string firstError = null;

                for (int i = 0; i < plan.Delete.Count; i++)
                {
                    try
                    {
                        File.Delete(Path.Combine(m_Dir, plan.Delete[i]));
                        deleted++;
                    }
                    catch (Exception e)
                    {
                        failed++;
                        if (firstError == null)
                        {
                            firstError = plan.Delete[i] + ": " + e.Message;
                        }
                    }
                }

                ReportCleanup(deleted, plan.LegacyCount, plan.Bytes, failed, firstError);
            }
            catch (Exception e)
            {
                // 扫目录本身失败（目录没了、没权限）。日志照写，只是这次没清理。
                LogSystem.InternalError("volume cleanup failed: " + e.Message);
            }
        }

        /// <summary>
        /// 清理结果记一条 Internal 日志（全局硬约束 6：走 Debug.Log 保证 logcat 可见）。
        /// <para>
        /// 什么都没删就不吭声 —— 绝大多数启动都属于这种，每次都刷一行只会淹没别的信息。
        /// </para>
        /// </summary>
        private static void ReportCleanup(int deleted, int legacy, long bytes, int failed, string firstError)
        {
            if (failed > 0)
            {
                LogSystem.InternalError(
                    "volume cleanup deleted " + deleted + " file(s), " + failed
                    + " failed (first: " + firstError + ")");
                return;
            }

            if (deleted > 0)
            {
                LogSystem.InternalInfo(
                    "volume cleanup deleted " + deleted + " file(s), " + legacy
                    + " legacy, " + (bytes / 1024) + " KB freed");
            }
        }

        /// <summary>
        /// 开本次会话的卷并让句柄常开。由 Attach 在主线程调用一次。
        ///
        /// <para>
        /// ⚠️ <c>FileShare.ReadWrite</c> 不是随手写的：票 11 的飞书一键发送要在**句柄常开的同时**
        /// 并发读这个文件。用默认的 FileShare.Read 会让一键发送直接读不到。
        /// </para>
        /// <para>
        /// FileMode.Append 而不是 Create：卷名带 traceId，撞名在实践中不会发生，
        /// 但万一撞了，追加也好过把别人的日志截断。
        /// </para>
        /// </summary>
        internal void Open()
        {
            if (m_Stream != null)
            {
                return;
            }

            Directory.CreateDirectory(m_Dir);
            m_Stream = new FileStream(
                m_FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, StreamBufferBytes, false);

            m_Bytes = m_Stream.Length;
            if (m_Bytes >= m_Config.VolumeMaxBytes)
            {
                m_Full = true;
            }
        }

        /// <summary>
        /// 把一批记录写进卷并 Flush()。**只由打包线程调用**，每次缓冲交换调一次，不攒批。
        ///
        /// <para>
        /// 敏感记录**照常写**（正文里的凭据是排查登录问题的必要材料，见全局硬约束 7）——
        /// 拦截只发生在批次累加器那一侧。
        /// </para>
        /// <para>
        /// 写盘失败一律吞掉并只报一次：日志系统把宿主搞挂是不可接受的。
        /// （旧 LogWrite 的 catch 是**空**的，连报都不报，IO 失败完全静默。）
        /// </para>
        /// <para>
        /// ⚠️ 票 06 起**返回本轮有没有发生写盘失败**，给 <see cref="LogFlushGate"/> 用。
        /// 失败照旧不外抛，但打包线程必须知道 —— 否则它只能报一个乐观的水位，
        /// 而"写盘失败也算这一轮结束"这条规则要求失败时同样推进水位（并留下诊断），
        /// 不然 <see cref="LogSystem.Flush"/> 会一直等到超时（票 06）。
        /// </para>
        /// </summary>
        /// <returns>
        /// false 仅表示**捕获到了 IO 异常**。"没东西可写""句柄已关""卷已满"都返回 true ——
        /// 那些是正常的硬停，不是故障。
        /// </returns>
        internal bool Write(IReadOnlyList<LogRecord> records)
        {
            if (m_Stream == null || m_Full || records == null || records.Count == 0)
            {
                return true;
            }

            try
            {
                for (int i = 0; i < records.Count; i++)
                {
                    LogRecord record = records[i];

                    WriteLine(record.Msg);
                    if (ShouldWriteStack(record))
                    {
                        WriteLine(record.Stack);
                    }

                    if (m_Bytes >= m_Config.VolumeMaxBytes)
                    {
                        WriteLine(FullNotice);
                        m_Full = true;
                        break;
                    }
                }

                // 不带 true。Flush() 把托管缓冲推给内核就够了 —— 进程被 SIGKILL，
                // 已经 write() 进内核的字节内核照样会刷盘；Flush(true) 是 fsync，会真的等磁盘。
                m_Stream.Flush();
                return true;
            }
            catch (Exception e)
            {
                ReportWriteFailure(e);
                return false;
            }
        }

        /// <summary>
        /// 关闭句柄。quit、以及 Editor 下的域重载前必须调用，否则句柄泄漏会导致
        /// 下次进播放模式打不开卷（票 03 的人工验收里有这一条）。
        /// <para>可重复调用；关闭过程中的任何异常都不向外抛。</para>
        /// </summary>
        internal void Close()
        {
            FileStream stream = m_Stream;
            m_Stream = null;
            if (stream == null)
            {
                return;
            }

            try
            {
                stream.Flush();
            }
            catch (Exception)
            {
                // 关闭路径上的失败没有补救手段，也没人还能看到诊断日志，只能咽下。
            }

            try
            {
                stream.Dispose();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 堆栈只对 Error / Assert / Exception 写进卷 —— 与重构前的 LogWrite 行为一致。
        /// <para>
        /// 项目的 ProjectSettings 对 LogType.Log 也开着 ScriptOnly 堆栈，
        /// 每条 Debug.Log 都带一份调用栈；全写进去会把卷撑成几倍大、且几乎没有排查价值。
        /// 上传侧要不要带堆栈是 S3 的事，<see cref="LogRecord.Stack"/> 始终完整保留。
        /// </para>
        /// </summary>
        private static bool ShouldWriteStack(LogRecord record)
        {
            if (string.IsNullOrEmpty(record.Stack))
            {
                return false;
            }

            return record.Type == UnityEngine.LogType.Error
                   || record.Type == UnityEngine.LogType.Assert
                   || record.Type == UnityEngine.LogType.Exception;
        }

        /// <summary>
        /// 把一行文本编码成 UTF-8 写进流。复用编码缓冲，**不做 string 级的整块分配** ——
        /// 重构前每帧一次 StringBuilder.ToString() 的整块分配正是要消掉的开销之一（ADR-0001）。
        /// </summary>
        private void WriteLine(string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                int max = Encoding.UTF8.GetMaxByteCount(text.Length);
                byte[] buffer = m_EncodeBuffer;
                if (max > buffer.Length)
                {
                    buffer = new byte[max];
                    if (max <= MaxRetainedEncodeBufferBytes)
                    {
                        m_EncodeBuffer = buffer;
                    }
                }

                int count = Encoding.UTF8.GetBytes(text, 0, text.Length, buffer, 0);
                m_Stream.Write(buffer, 0, count);
                m_Bytes += count;
            }

            m_Stream.WriteByte(LineBreak);
            m_Bytes += 1;
        }

        /// <summary>
        /// 报告写盘失败（全局硬约束 6：走 Debug.Log 保证 logcat 可见）。只报第一次。
        /// </summary>
        private void ReportWriteFailure(Exception e)
        {
            if (m_WriteFailureReported)
            {
                return;
            }

            m_WriteFailureReported = true;
            LogSystem.InternalError("volume write failed: " + e.Message);
        }
    }
}
