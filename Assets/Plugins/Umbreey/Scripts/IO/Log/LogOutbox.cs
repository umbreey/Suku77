using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Umbreey
{
    /// <summary>
    /// 待传队列（术语见 CONTEXT.md）：磁盘上尚未上传成功的批次集合，**一个文件一个批次**。
    /// 它是上传的唯一数据来源 —— 日志先落盘再发送，从不直接从内存发出，
    /// 因此进程崩溃不会带走已打包的日志（票 08 / specs 0003「outbox 文件」/ 契约 C3）。
    ///
    /// <para>
    /// 职责被切成三层，删错代价最高的那层是纯的：
    /// <list type="bullet">
    ///   <item><b>纯函数</b>（<see cref="SelectForDeletion"/> / <see cref="SelectExpired"/> /
    ///   <see cref="SelectOverCapacity"/> / <see cref="TryParseCreateUnixSec"/> /
    ///   <see cref="BuildFileName"/>）—— 输入「文件名 + 大小」列表、当前时刻、配置，
    ///   输出「该删哪些」。不碰文件系统、不看时钟、不读全局状态，每个分支都能在 EditMode 里摆出来。
    ///   与票 04 的 <see cref="LogVolume.SelectForDeletion"/> 是同一套做法。</item>
    ///   <item><b>启动一次性 IO</b>（<see cref="Start"/>）—— 清 <c>.tmp</c>、按 TTL 与容量删、建索引。</item>
    ///   <item><b>运行期 IO</b>（<see cref="Submit(LogBatchPayload)"/>）—— 原子写一个批次，
    ///   写之前先按**索引**淘汰，**不重扫目录**。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// ⚠️ **容量是运行期的实时上限，不是"启动时扫一遍"**（specs 0003）：
    /// 启动时 49MB、运行中再写 20MB 就会突破到 69MB。所以启动时建「文件名 → 大小」索引，
    /// 之后写入/删除**增量维护**它，提交新批次前先按索引淘汰。
    /// 目录扫描（TTL / <c>.tmp</c> 清理 / 建索引）确实只在启动时做一次 —— 那三件事不随运行增长；
    /// **但容量判定一直在线**。两句话不矛盾，别读成"容量只管一次"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 跑在**打包线程**上（<see cref="Submit(LogBatchPayload)"/> 的唯一调用者是
    /// <see cref="LogBatchSink.Write"/>），因此**不得调用任何 Unity API**（全局硬约束 1）。
    /// <see cref="Directory"/> / <see cref="File"/> / <see cref="DateTime"/> 都是 BCL，安全；
    /// 诊断日志走 <see cref="LogSystem.InternalError"/>（<c>Debug.Log*</c> 是线程安全的，
    /// 且全局硬约束 6 明确要求它，才能在 logcat 里看见）。
    /// **也不得抛异常**：本类整段吞异常，最坏是这一批没落盘。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 内容是 **payload_raw 明文 JSON，不压缩** —— 压缩只在发送那一刻做（票 09）。
    /// 明文的收益是联调期能直接 <c>cat</c> 出来看，排查成本低一个量级。
    /// </para>
    /// </summary>
    internal sealed class LogOutbox
    {
        // ════════════════════════════════════════════════════════════════════════════════
        //  文件名与目录（契约 C3，逐字实现）
        //
        //    {日志目录}/outbox/{createUnixSec:D10}-{traceId 前 8 位}-{seq:D5}.json
        //
        //  · 字典序 == 时间序 == FIFO 发送顺序（D10 补零是这条成立的前提：
        //    不补零的话 "999999999" 会排在 "1000000000" 后面）
        //  · traceId 段同时隔离**多进程**（两个实例 traceId 不同）与**时钟回拨重名**
        //    （新会话的 createSec 变小、可能撞上上次会话的残留），**因此不需要 PID 后缀**
        //  · createUnixSec 只用于 TTL 淘汰；重签名窗口的判定用单调时钟（票 09），不用它
        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>outbox 目录名，挂在 <see cref="LogSystem.LogDir"/> 下。</summary>
        internal const string DirectoryName = "outbox";

        /// <summary>批次文件的扩展名。**扫描只认它**（契约 C3）。</summary>
        internal const string Extension = ".json";

        /// <summary>写入中的临时扩展名。扫描永远不认它，启动时清理。</summary>
        internal const string TempExtension = ".tmp";

        /// <summary>createUnixSec 段的补零位数。见本节顶部关于"字典序 == 时间序"的说明。</summary>
        internal const int CreateSecDigits = 10;

        /// <summary>seq 段的补零位数。</summary>
        internal const int SeqDigits = 5;

        /// <summary>三段之间的分隔符。</summary>
        internal const char Separator = '-';

        /// <summary>
        /// **Editor 下每次启动清空 outbox**（票 08 / specs 0003）——
        /// Editor 不上传，留着只会一路堆到容量上限，而它唯一的用途是让开发者看一眼批次格式。
        /// <para>
        /// 用 <c>#if</c> 而不是 <c>Application.isEditor</c>：本类跑在打包线程上，
        /// 碰任何 Unity API 都违反全局硬约束 1。预处理符号是编译期常量，零成本也零风险。
        /// </para>
        /// <para>
        /// ⚠️ 这是**唯一**的平台分支。Standalone 会产出永远不会被发送的文件，
        /// 那是有意为之：换来全平台一套逻辑，代价只是最多 <see cref="LogConfig.OutboxMaxBytes"/> 磁盘。
        /// 不要为它再加分支。
        /// </para>
        /// </summary>
#if UNITY_EDITOR
        internal const bool ClearOnStartupByDefault = true;
#else
        internal const bool ClearOnStartupByDefault = false;
#endif

        /// <summary>
        /// 合法 createUnixSec 的上界（9999-12-31）。超出它的一律当作"不是我们写的名字"：
        /// <see cref="DateTime.AddSeconds"/> 溢出会抛异常，而保留策略里**任何异常都是灾难**
        /// （一次都别想删成功）。
        /// </summary>
        private const long MaxUnixSec = 253402300799L;

        /// <summary>FileStream 自己的缓冲。整批一次写完，它只影响单次 write 的合并粒度。</summary>
        private const int StreamBufferBytes = 4096;

        private static readonly DateTime UnixEpochUtc =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>"D10"。const 表达式里拼不出来（string + int 不是常量表达式），故用 readonly。</summary>
        private static readonly string CreateSecFormat =
            "D" + CreateSecDigits.ToString(CultureInfo.InvariantCulture);

        private static readonly string SeqFormat =
            "D" + SeqDigits.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// 当前进程的 outbox。**票 09 的上传器要拿它来删已发送的文件** ——
        /// 绕过本类直接 <c>File.Delete</c> 会让索引虚高，容量上限从此失准，
        /// 所以删除必须走 <see cref="Delete"/>。
        /// <para><see cref="Create"/> 之前是 null（宿主还没调 <see cref="LogSystem.Init"/>）。</para>
        /// </summary>
        internal static LogOutbox Instance { get; private set; }

        private readonly LogConfig m_Config;
        private readonly string m_Dir;
        private readonly string m_Trace;

        /// <summary>
        /// 「文件名 → 字节数」索引，**这就是容量上限的事实源**。
        /// <para>
        /// SortedDictionary + <see cref="StringComparer.Ordinal"/>：键的顺序天然就是
        /// 字典序，也就是时间序，也就是 FIFO —— 淘汰"最旧"就是从头取，
        /// 发送顺序（票 09）也是从头取，两处不需要各自再排一次。
        /// </para>
        /// </summary>
        private readonly SortedDictionary<string, long> m_Index =
            new SortedDictionary<string, long>(StringComparer.Ordinal);

        /// <summary>
        /// 索引的锁。写入只发生在打包线程，但**删除会由上传线程发起**（票 09 发送成功后），
        /// 两条线程都要维护索引。锁只护内存里的字典，**不护文件 IO** —— 那会把两条线程串起来。
        /// </summary>
        private readonly object m_Lock = new object();

        /// <summary>索引里所有文件的字节数之和。与 <see cref="m_Index"/> 同锁维护。</summary>
        private long m_Bytes;

        /// <summary>进程内批次序号，进文件名的 seq 段。</summary>
        private int m_Seq;

        private bool m_Started;

        /// <summary>目录可用。<see cref="Start"/> 失败时为 false，此后 Submit 直接空转。</summary>
        private bool m_Ready;

        /// <summary>写盘失败只报一次。没有这个闩，一次磁盘满能刷出每 3 秒一条的错误。</summary>
        private bool m_WriteFailureReported;

        /// <summary>淘汰通告的限频（复用 <see cref="LogConfig.SuppressNoticeSec"/>，票 05 的裁定）。</summary>
        private bool m_EvictNoticeSeen;

        private long m_EvictNoticeUptimeMs;

        private int m_PendingEvicted;

        private long m_PendingEvictedBytes;

        /// <summary>
        /// Editor 下每次启动清空 outbox。默认值来自 <see cref="ClearOnStartupByDefault"/>，
        /// 单元测试显式覆盖它（测试自己也跑在 Editor 里，不覆盖的话保留策略一条都测不到）。
        /// </summary>
        internal bool ClearAllOnStartup = ClearOnStartupByDefault;

        /// <param name="cfg">读取 <see cref="LogConfig.OutboxMaxBytes"/> / <see cref="LogConfig.OutboxKeepDays"/>。</param>
        /// <param name="logDir">日志目录（主线程预取的 persistentDataPath）。outbox 是它的子目录。</param>
        /// <param name="traceId">本次会话的追踪标识，取前 8 位进文件名。</param>
        internal LogOutbox(LogConfig cfg, string logDir, string traceId)
        {
            m_Config = cfg ?? new LogConfig();
            m_Dir = string.IsNullOrEmpty(logDir) ? "" : Path.Combine(logDir, DirectoryName);
            m_Trace = TracePart(traceId);
        }

        /// <summary>
        /// 建（或复用）本进程的 outbox。由 <see cref="LogSystem.Init"/> 的那一行接线调用。
        /// <para>
        /// 构造**不做任何 IO**：目录扫描留给打包线程第一次 <see cref="Submit(LogBatchPayload)"/>
        /// 时做（<see cref="Start"/>），理由与票 03 把 <see cref="LogVolume.CleanupOnStartup"/>
        /// 放进打包线程一样 —— 玩家设备上可能积着几百个批次文件，把这次扫描放进主线程的
        /// Init 就是启动卡顿。
        /// </para>
        /// <para>重复 Init 时参数没变就复用同一个实例，免得两个索引各算各的容量。</para>
        /// </summary>
        internal static LogOutbox Create(LogConfig cfg, string logDir, string traceId)
        {
            LogOutbox existing = Instance;
            string dir = string.IsNullOrEmpty(logDir) ? "" : Path.Combine(logDir, DirectoryName);
            if (existing != null
                && string.Equals(existing.m_Dir, dir, StringComparison.Ordinal)
                && string.Equals(existing.m_Trace, TracePart(traceId), StringComparison.Ordinal))
            {
                return existing;
            }

            LogOutbox created = new LogOutbox(cfg, logDir, traceId);
            Instance = created;
            return created;
        }

        /// <summary>outbox 目录的完整路径。刻意不叫 Directory：那会遮蔽 <see cref="System.IO.Directory"/>。</summary>
        internal string DirPath
        {
            get { return m_Dir; }
        }

        /// <summary>索引里的总字节数 —— 容量上限就是拿它比的。</summary>
        internal long Bytes
        {
            get
            {
                lock (m_Lock)
                {
                    return m_Bytes;
                }
            }
        }

        /// <summary>索引里的文件数。</summary>
        internal int Count
        {
            get
            {
                lock (m_Lock)
                {
                    return m_Index.Count;
                }
            }
        }

        /// <summary>
        /// 待发送的文件名，**按 FIFO 顺序**（字典序 == 时间序）。票 09 的发送循环从头取。
        /// </summary>
        internal List<string> PendingNames()
        {
            lock (m_Lock)
            {
                return new List<string>(m_Index.Keys);
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  启动：清 .tmp → 按 TTL / 容量删 → 建索引。**整个进程只做一次。**
        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 启动一次性清理并建立索引。由打包线程在第一次落盘前调用（<see cref="EnsureStarted"/>），
        /// 重复调用是空转。
        ///
        /// <para>顺序有讲究：</para>
        /// <list type="number">
        ///   <item><b>先清 <c>.tmp</c></b> —— 它们不参与容量与 TTL（扫描不认 <c>.tmp</c>），
        ///   先清掉才不会在后面的统计里露头。</item>
        ///   <item><b>再扫 <c>.json</c> 建候选列表</b>。</item>
        ///   <item><b>最后按纯函数的计划删</b>，剩下的进索引。</item>
        /// </list>
        ///
        /// <para>
        /// **任何失败都不外抛、不中断启动**：目录建不出来、权限不对、文件被占用 ——
        /// 最坏是这次少删几个、或者这次不落盘，绝不能让日志系统把宿主搞挂。
        /// </para>
        /// </summary>
        internal void Start()
        {
            if (m_Started)
            {
                return;
            }

            m_Started = true;

            if (m_Dir.Length == 0)
            {
                // Attach 失败时 LogDir 是空串。没有目录就没有 outbox，静默退出 ——
                // 这时候 Attach 自己已经报过错了，再报一条只是噪音。
                return;
            }

            try
            {
                Directory.CreateDirectory(m_Dir);

                DateTime utcNow = DateTime.UtcNow;

                int tmpSkipped;
                int tmpDeleted = CleanupTemp(m_Dir, utcNow, out tmpSkipped);

                List<LogOutboxFile> files = ScanFiles(m_Dir);

                List<string> doomed = ClearAllOnStartup
                    ? NamesOf(files)
                    : SelectForDeletion(files, utcNow, m_Config);

                Dictionary<string, long> sizes = SizeMap(files);
                var chosen = new HashSet<string>(doomed, StringComparer.Ordinal);
                int deleted = 0;
                long freed = 0;

                lock (m_Lock)
                {
                    m_Index.Clear();
                    m_Bytes = 0;

                    for (int i = 0; i < files.Count; i++)
                    {
                        LogOutboxFile file = files[i];
                        if (chosen.Contains(file.Name))
                        {
                            continue;
                        }

                        m_Index[file.Name] = file.Bytes;
                        m_Bytes += file.Bytes;
                    }
                }

                for (int i = 0; i < doomed.Count; i++)
                {
                    long bytes = SizeOf(sizes, doomed[i]);
                    if (TryDeleteFile(Path.Combine(m_Dir, doomed[i])))
                    {
                        deleted++;
                        freed += bytes;
                    }
                    else
                    {
                        // 删不掉的文件仍然占着磁盘 —— 让它回到索引里，
                        // 否则容量上限会按"它不存在"来算，实际占用悄悄突破上限。
                        lock (m_Lock)
                        {
                            if (!m_Index.ContainsKey(doomed[i]))
                            {
                                m_Index[doomed[i]] = bytes;
                                m_Bytes += bytes;
                            }
                        }
                    }
                }

                m_Ready = true;
                ReportStartup(deleted, freed, tmpDeleted, tmpSkipped);
            }
            catch (Exception e)
            {
                // 目录本身建不出来 / 扫不动。日志照采集、照写卷，只是这次没有 outbox。
                LogSystem.InternalError("outbox start failed: " + e.Message);
            }
        }

        /// <summary>
        /// 清理遗留的 <c>.tmp</c>：进程在写文件的一瞬间被杀会留下它们，
        /// 而"进程被杀"恰恰是整套设计的核心场景。
        ///
        /// <para>
        /// ⚠️ **跳过 mtime 在 <see cref="LogVolume.ActiveVolumeGraceSec"/> 之内的文件**（契约 C3）——
        /// PC 多开时那可能是**另一个实例正在写**的临时文件，删掉会让对方的批次凭空消失。
        /// 这个宽限期与卷保留里"跳过另一个实例的活跃卷"是**同一个值**（C3 补记：两处必须同值，
        /// 否则多开时一边保、一边删），所以直接引用票 04 已经落成的那个常量，不另写一份。
        /// </para>
        /// <para>
        /// mtime 落在未来（系统时钟被回拨、文件从别的机器拷来）时一律当作"正在被写"：
        /// 减法得负数，天然小于宽限期，不需要额外分支。这种文件我们对它一无所知，不删是唯一安全的选择。
        /// </para>
        /// </summary>
        private static int CleanupTemp(string dir, DateTime utcNow, out int skipped)
        {
            skipped = 0;
            int deleted = 0;

            string[] paths = Directory.GetFiles(dir);
            for (int i = 0; i < paths.Length; i++)
            {
                try
                {
                    var info = new FileInfo(paths[i]);
                    if (!info.Exists
                        || !info.Name.EndsWith(TempExtension, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if ((utcNow - info.LastWriteTimeUtc).TotalSeconds < LogVolume.ActiveVolumeGraceSec)
                    {
                        skipped++;
                        continue;
                    }

                    info.Delete();
                    deleted++;
                }
                catch (Exception)
                {
                    // 单个文件读不出属性 / 删不掉：读不到 = 不敢删，安全的那一侧。
                }
            }

            return deleted;
        }

        /// <summary>
        /// 扫出目录里**能认出名字的 <c>.json</c>**，按从旧到新排。
        ///
        /// <para>
        /// **只认 <c>.json</c>**（契约 C3）：<c>.tmp</c> 可能是半个文件，发出去必然 code=1，
        /// 白费 5 次重试预算，日志还是丢了。
        /// </para>
        /// <para>
        /// 名字认不出来的 <c>.json</c> 一律**忽略**：不返回、不删除、不计入容量。
        /// 歧义一律倒向"别动它" —— 这个目录里删错一个文件就是丢一批日志。
        /// </para>
        /// </summary>
        internal static List<LogOutboxFile> ScanFiles(string dir)
        {
            var files = new List<LogOutboxFile>();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return files;
            }

            string[] paths = Directory.GetFiles(dir);
            for (int i = 0; i < paths.Length; i++)
            {
                try
                {
                    var info = new FileInfo(paths[i]);
                    if (!info.Exists)
                    {
                        continue;
                    }

                    long createSec;
                    if (!TryParseCreateUnixSec(info.Name, out createSec))
                    {
                        continue;
                    }

                    files.Add(new LogOutboxFile(info.Name, info.Length));
                }
                catch (Exception)
                {
                }
            }

            SortOldestFirst(files);
            return files;
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  运行期：原子写一个批次
        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 落一个批次。这是 <see cref="LogBatchHandler"/> 的实现，
        /// 由 <see cref="LogBatchSink.Write"/> 在打包线程上逐批调用。
        /// </summary>
        internal void Submit(LogBatchPayload payload)
        {
            if (payload == null)
            {
                return;
            }

            SubmitBytes(payload.Bytes);
        }

        /// <summary>
        /// 落一段 payload_raw，返回写成的文件名（失败返回 null）。
        /// createUnixSec 取**墙上时钟** —— 这是全局硬约束 2 明确列出的三个合法用途之一
        /// （文件名里的 createUnixSec），因为它要被下次启动的 TTL 判定读到，
        /// 而单调时钟跨不过进程。
        /// </summary>
        internal string SubmitBytes(byte[] payloadRaw)
        {
            return SubmitAt(payloadRaw, NowUnixSec());
        }

        /// <summary>
        /// <see cref="SubmitBytes"/> 的可注入时刻版本 —— 时钟由调用方给，
        /// 于是"时钟回拨""TTL 过期"这些用例能在 EditMode 里直接摆出来。
        ///
        /// <para>顺序是硬要求，反了就不成立：</para>
        /// <list type="number">
        ///   <item><b>先按索引淘汰</b>（<see cref="TrimForIncoming"/>）——
        ///   容量必须是**运行期实时上限**，写完再补淘汰就已经突破过了。</item>
        ///   <item><b>再原子写</b>（<see cref="WriteAtomic"/>）。</item>
        ///   <item><b>写成功才进索引</b> —— 失败的文件不占预算。</item>
        /// </list>
        /// </summary>
        internal string SubmitAt(byte[] payloadRaw, long createUnixSec)
        {
            if (payloadRaw == null || payloadRaw.Length == 0)
            {
                return null;
            }

            try
            {
                EnsureStarted();
                if (!m_Ready)
                {
                    return null;
                }

                TrimForIncoming(payloadRaw.Length);

                string name = BuildFileName(createUnixSec, m_Trace, NextSeq());
                if (!WriteAtomic(name, payloadRaw))
                {
                    return null;
                }

                lock (m_Lock)
                {
                    long previous;
                    if (m_Index.TryGetValue(name, out previous))
                    {
                        m_Bytes -= previous;
                    }

                    m_Index[name] = payloadRaw.Length;
                    m_Bytes += payloadRaw.Length;
                }

                return name;
            }
            catch (Exception e)
            {
                // 打包线程不能因为一次落盘失败就没了 —— 本地卷还得继续写。
                ReportWriteFailure("submit", e);
                return null;
            }
        }

        /// <summary>
        /// 删一个批次文件并**同步维护索引**。票 09 发送成功后调用它，容量淘汰也走它 ——
        /// 两条路共用同一段代码，索引不可能只在一边被更新。
        /// </summary>
        /// <returns>true = 文件确实不在了（本来就不存在也算）。</returns>
        internal bool Delete(string name)
        {
            if (string.IsNullOrEmpty(name) || m_Dir.Length == 0)
            {
                return false;
            }

            if (!TryDeleteFile(Path.Combine(m_Dir, name)))
            {
                return false;
            }

            lock (m_Lock)
            {
                long bytes;
                if (m_Index.TryGetValue(name, out bytes))
                {
                    m_Index.Remove(name);
                    m_Bytes -= bytes;
                }
            }

            return true;
        }

        private void EnsureStarted()
        {
            if (!m_Started)
            {
                Start();
            }
        }

        /// <summary>
        /// **提交新批次之前**按索引淘汰到"装得下这一批"为止（specs 0003）。
        ///
        /// <para>
        /// ⚠️ 判定用的是 <see cref="m_Bytes"/> + 这一批的字节数，**不重扫目录**：
        /// 每 3 秒扫一次目录既慢又没必要，索引是增量维护的，它就是事实源。
        /// </para>
        /// <para>
        /// 常见情形（没超限）只做一次加法就返回 —— 快照与纯函数只在真的要删时才构造。
        /// </para>
        /// <para>
        /// 单批自己就大过 <see cref="LogConfig.OutboxMaxBytes"/> 时会把索引清空后照写不误：
        /// 一个批次最坏也就几百 KB（msg 与 stack 都已截断），而丢弃等于让最该看的那条日志消失。
        /// </para>
        /// </summary>
        private void TrimForIncoming(long incomingBytes)
        {
            List<LogOutboxFile> snapshot;

            lock (m_Lock)
            {
                if (m_Bytes + incomingBytes <= m_Config.OutboxMaxBytes)
                {
                    return;
                }

                snapshot = SnapshotLocked();
            }

            List<string> doomed = SelectOverCapacity(snapshot, incomingBytes, m_Config);
            Dictionary<string, long> sizes = SizeMap(snapshot);

            int evicted = 0;
            long freed = 0;
            for (int i = 0; i < doomed.Count; i++)
            {
                long bytes = SizeOf(sizes, doomed[i]);
                if (Delete(doomed[i]))
                {
                    evicted++;
                    freed += bytes;
                }
            }

            ReportEviction(evicted, freed);
        }

        /// <summary>
        /// 原子写：先写 <c>.tmp</c>，<b>Flush 之后</b> <see cref="File.Move"/> 成 <c>.json</c>
        /// （同一文件系统上的重命名是原子的）。**这是硬要求**（契约 C3 / 票 08）。
        ///
        /// <para>
        /// 不这么做的后果不是"文件难看"，而是：写到一半被杀 → 半个 JSON → 下次启动扫到 →
        /// 发送 → 服务端 code=1 → 退避重试 5 次 → 丢弃。白费 5 次请求，日志还是丢了。
        /// </para>
        /// <para>
        /// ⚠️ **这里是普通 <see cref="FileStream.Flush()"/>，不是 <c>Flush(true)</c>，
        /// 而且不该改成 <c>Flush(true)</c>**（ADR-0001）。下一个读到这里的人多半会想
        /// "落盘不是该 fsync 吗"，所以理由写在这儿：
        /// <list type="number">
        ///   <item>
        ///     **我们保的是"进程被杀"，那个场景普通 Flush 就够了。** 普通 Flush 把字节
        ///     交给内核页缓存，进程随后被 SIGKILL，内核照样把它刷到盘上；紧接着的
        ///     <see cref="File.Move"/> 是元数据操作。fsync 多买到的是抗**掉电**，
        ///     而 ADR-0001 明确把掉电划到范围外（"只有断电或内核 panic 才会丢"）。
        ///   </item>
        ///   <item>
        ///     **代价落在别人身上。** 两个接收端共享同一个打包线程，
        ///     ADR-0001 专门警告过这条耦合：**outbox 写盘慢会延后下一轮本地写盘**。
        ///     每 3 秒一次 fsync 直接侵蚀 ADR 承诺的本地卷延迟（正常负载 ~1ms 量级）——
        ///     Android flash 上 fsync 在存储繁忙时能到几十上百毫秒，
        ///     而那恰恰是最不该让本地卷排队等的时候。
        ///   </item>
        /// </list>
        /// 一句话：<c>Flush(true)</c> 是**为一个划到范围外的场景，付一个 ADR 明确警告过的代价**。
        /// "不丢日志"的正确机制不是频繁 fsync，而是尽快把字节交给内核 —— 加上这里的原子写。
        /// </para>
        /// <para>
        /// 失败时把 <c>.tmp</c> 顺手删掉：留着它下次启动也会被清理（那时它已经过了宽限期），
        /// 但当场删掉更干净，也不会在多开时被对方误判成"活跃临时文件"。
        /// </para>
        /// </summary>
        private bool WriteAtomic(string name, byte[] bytes)
        {
            string stem = name.Substring(0, name.Length - Extension.Length);
            string tmpPath = Path.Combine(m_Dir, stem + TempExtension);
            string finalPath = Path.Combine(m_Dir, name);

            try
            {
                using (var stream = new FileStream(
                    tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    StreamBufferBytes, FileOptions.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }

                File.Move(tmpPath, finalPath);
                return true;
            }
            catch (Exception e)
            {
                ReportWriteFailure(name, e);
                TryDeleteFile(tmpPath);
                return false;
            }
        }

        private int NextSeq()
        {
            return m_Seq++;
        }

        private static long NowUnixSec()
        {
            return (long)(DateTime.UtcNow - UnixEpochUtc).TotalSeconds;
        }

        private static bool TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private List<LogOutboxFile> SnapshotLocked()
        {
            var snapshot = new List<LogOutboxFile>(m_Index.Count);
            foreach (KeyValuePair<string, long> entry in m_Index)
            {
                snapshot.Add(new LogOutboxFile(entry.Key, entry.Value));
            }

            return snapshot;
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  文件名：构造与解析（纯函数）
        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 拼一个 outbox 文件名：<c>{createUnixSec:D10}-{trace8}-{seq:D5}.json</c>（契约 C3）。
        /// 纯函数，时间与序号都由调用方给，因此可以在 EditMode 里直接测。
        /// <para>
        /// createUnixSec 为负（玩家把系统时间调到 1970 年之前）时夹到 0：
        /// 负号会多出一个 <see cref="Separator"/>，把三段结构撑坏。
        /// </para>
        /// <para>
        /// ⚠️ 补零必须用 <see cref="CultureInfo.InvariantCulture"/>：设备的区域可能带
        /// 非 ASCII 数字或分组符号，票 04 已经被佛历坑过一次，写与读两边都必须是不变文化。
        /// </para>
        /// </summary>
        internal static string BuildFileName(long createUnixSec, string traceId, int seq)
        {
            if (createUnixSec < 0)
            {
                createUnixSec = 0;
            }

            if (seq < 0)
            {
                seq = 0;
            }

            return createUnixSec.ToString(CreateSecFormat, CultureInfo.InvariantCulture)
                   + Separator + TracePart(traceId)
                   + Separator + seq.ToString(SeqFormat, CultureInfo.InvariantCulture)
                   + Extension;
        }

        /// <summary>
        /// 从文件名取出 createUnixSec。**认不出来就返回 false，绝不抛异常** ——
        /// 这个目录里可能躺着别人留下的文件、上一版客户端的文件、拷进来的文件，
        /// 一个畸形名字就掀翻整次清理是不能接受的。
        ///
        /// <para>判定逐段收紧，任何一段不符就是"不认识"：</para>
        /// <list type="number">
        ///   <item>扩展名必须是 <see cref="Extension"/></item>
        ///   <item>按 <see cref="Separator"/> 切开后至少三段</item>
        ///   <item>第一段是**至少 <see cref="CreateSecDigits"/> 位的纯数字**
        ///   （不足 10 位说明不是我们补零写出来的，"字典序 == 时间序"也就不成立了）</item>
        ///   <item>最后一段（seq）是纯数字</item>
        ///   <item>数值不超过 <see cref="MaxUnixSec"/>，否则后面按日期算年龄会溢出</item>
        /// </list>
        /// <para>
        /// 中间的 trace 段不做校验：它只负责"不撞车"，长什么样不影响时间序，
        /// 校验它只会在 traceId 形态变化时把自己的文件判成陌生文件。
        /// </para>
        /// </summary>
        internal static bool TryParseCreateUnixSec(string fileName, out long createUnixSec)
        {
            createUnixSec = 0;

            if (string.IsNullOrEmpty(fileName)
                || !fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string stem = fileName.Substring(0, fileName.Length - Extension.Length);
            string[] parts = stem.Split(Separator);
            if (parts.Length < 3)
            {
                return false;
            }

            string head = parts[0];
            string tail = parts[parts.Length - 1];
            if (head.Length < CreateSecDigits || !IsDigits(head) || !IsDigits(tail))
            {
                return false;
            }

            long parsed;
            if (!long.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)
                || parsed > MaxUnixSec)
            {
                return false;
            }

            createUnixSec = parsed;
            return true;
        }

        private static bool IsDigits(string text)
        {
            if (text.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] < '0' || text[i] > '9')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// traceId 的前 8 位，清洗成能安全进文件名的形式。
        /// <para>
        /// 位数与清洗规则都复用票 03/04 的 <see cref="LogVolume"/> —— 卷名与 outbox 文件名
        /// 用的是同一个 traceId 的同一段，两处各写一份迟早会漂移。
        /// </para>
        /// </summary>
        private static string TracePart(string traceId)
        {
            string trace = LogVolume.SanitizeFileNamePart(traceId);
            return trace.Length > LogVolume.TraceIdPrefixLength
                ? trace.Substring(0, LogVolume.TraceIdPrefixLength)
                : trace;
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  保留策略（纯函数）
        //
        //  ⚠️ 与票 04 的卷保留同样的道理：**删错 = 丢日志**。所以"选"与"删"分开，
        //  选的这一半不碰文件系统、不看时钟（当前时刻由调用方传入），
        //  每个分支都能在 EditMode 里摆出来直接断言。
        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>保留策略的输入项：只需要文件名与大小两件事。</summary>
        internal struct LogOutboxFile
        {
            /// <summary>文件名，**含扩展名、不含目录**。createUnixSec 从它解析。</summary>
            internal readonly string Name;

            /// <summary>字节数，用于 <see cref="LogConfig.OutboxMaxBytes"/> 的预算。</summary>
            internal readonly long Bytes;

            internal LogOutboxFile(string name, long bytes)
            {
                Name = name;
                Bytes = bytes;
            }
        }

        /// <summary>
        /// **纯函数**：启动时该删哪些。两条规则**取并集、不重复删**，结果按从旧到新排。
        /// <list type="number">
        ///   <item>TTL <see cref="LogConfig.OutboxKeepDays"/>（<see cref="SelectExpired"/>）</item>
        ///   <item>容量 <see cref="LogConfig.OutboxMaxBytes"/>，超限**丢最旧**（<see cref="SelectOverCapacity"/>）</item>
        /// </list>
        /// <para>
        /// 先 TTL 后容量：过期的反正要删，让它先退出预算，容量这一条才不会多删一个还活着的批次。
        /// </para>
        /// </summary>
        internal static List<string> SelectForDeletion(
            IReadOnlyList<LogOutboxFile> files, DateTime utcNow, LogConfig cfg)
        {
            var doomed = new List<string>();
            if (files == null || files.Count == 0 || cfg == null)
            {
                return doomed;
            }

            List<string> expired = SelectExpired(files, utcNow, cfg);
            var chosen = new HashSet<string>(expired, StringComparer.Ordinal);

            var survivors = new List<LogOutboxFile>(files.Count);
            for (int i = 0; i < files.Count; i++)
            {
                if (!chosen.Contains(files[i].Name))
                {
                    survivors.Add(files[i]);
                }
            }

            List<string> overflow = SelectOverCapacity(survivors, 0, cfg);
            for (int i = 0; i < overflow.Count; i++)
            {
                chosen.Add(overflow[i]);
            }

            // 统一按从旧到新输出：调用方（和测试）拿到的顺序与"删的是最旧的"这句话一致。
            List<LogOutboxFile> sorted = Sorted(files);
            for (int i = 0; i < sorted.Count; i++)
            {
                if (chosen.Contains(sorted[i].Name))
                {
                    doomed.Add(sorted[i].Name);
                }
            }

            return doomed;
        }

        /// <summary>
        /// **纯函数**：TTL 过期的那些。
        ///
        /// <para>
        /// ⚠️ 天数判定用**墙上时钟**（文件名里的 createUnixSec）——
        /// 这是全局硬约束 2 明确列出的例外之一。单调时钟跨不过进程，
        /// 而 TTL 恰恰是跨会话的：**它是防止永久失败的批次无限滞留的唯一机制**。
        /// 重试预算是内存态、进程重启就清零，所以"重试 5 次丢弃"实际是"每次会话重试 5 次"，
        /// 一个因配置错误而永远失败的批次会在每次启动都烧 5 次请求、永远占着空间，
        /// 拦得住它的只有 TTL。
        /// </para>
        /// <para>
        /// 日期落在未来（系统时间被回拨）时年龄是负数，永远不会 ≥ KeepDays ——
        /// 时钟乱掉时的方向是"少删"，不是"多删"。与票 04 的卷保留同一套取舍。
        /// </para>
        /// </summary>
        internal static List<string> SelectExpired(
            IReadOnlyList<LogOutboxFile> files, DateTime utcNow, LogConfig cfg)
        {
            var doomed = new List<string>();
            if (files == null || cfg == null || cfg.OutboxKeepDays <= 0)
            {
                return doomed;
            }

            List<LogOutboxFile> sorted = Sorted(files);
            for (int i = 0; i < sorted.Count; i++)
            {
                long createSec;
                if (!TryParseCreateUnixSec(sorted[i].Name, out createSec))
                {
                    continue;
                }

                if (AgeInDays(createSec, utcNow) >= cfg.OutboxKeepDays)
                {
                    doomed.Add(sorted[i].Name);
                }
            }

            return doomed;
        }

        /// <summary>
        /// **纯函数**：为了让 <paramref name="incomingBytes"/> 这一批装得下，该丢哪些**最旧的**。
        ///
        /// <para>
        /// <paramref name="incomingBytes"/> 是这条规则能成为"运行期实时上限"的关键：
        /// 启动清理时传 0（只管现存的），提交新批次前传这一批的字节数（先腾地方再写）。
        /// </para>
        /// <para>
        /// 丢**最旧**而不是最新：最新的批次离崩溃现场最近，信息密度最高。
        /// </para>
        /// </summary>
        internal static List<string> SelectOverCapacity(
            IReadOnlyList<LogOutboxFile> files, long incomingBytes, LogConfig cfg)
        {
            var doomed = new List<string>();
            if (files == null || cfg == null)
            {
                return doomed;
            }

            long total = incomingBytes;
            for (int i = 0; i < files.Count; i++)
            {
                total += files[i].Bytes;
            }

            if (total <= cfg.OutboxMaxBytes)
            {
                return doomed;
            }

            List<LogOutboxFile> sorted = Sorted(files);
            for (int i = 0; i < sorted.Count && total > cfg.OutboxMaxBytes; i++)
            {
                doomed.Add(sorted[i].Name);
                total -= sorted[i].Bytes;
            }

            return doomed;
        }

        /// <summary>
        /// 从旧到新排。**字典序就是时间序** —— createUnixSec 补零到 10 位在最前面，
        /// 所以按序数比较文件名即可，不必解析日期。
        /// <para>
        /// 用序数比较而不是文化相关比较：后者在某些区域会把 '-' 当作可忽略字符，
        /// 排出来的顺序随设备语言变化。
        /// </para>
        /// </summary>
        internal static void SortOldestFirst(List<LogOutboxFile> files)
        {
            if (files == null || files.Count < 2)
            {
                return;
            }

            files.Sort(CompareByName);
        }

        private static int CompareByName(LogOutboxFile a, LogOutboxFile b)
        {
            return string.CompareOrdinal(a.Name, b.Name);
        }

        private static List<LogOutboxFile> Sorted(IReadOnlyList<LogOutboxFile> files)
        {
            var copy = new List<LogOutboxFile>(files.Count);
            for (int i = 0; i < files.Count; i++)
            {
                copy.Add(files[i]);
            }

            SortOldestFirst(copy);
            return copy;
        }

        /// <summary>
        /// 批次的年龄，单位整日，从文件名里的 createUnixSec 算。
        /// 两边都取 UTC 的日期部分 —— 写文件名用的是 Unix 秒（UTC），
        /// 判定就必须也用 UTC，混本地时区会在 UTC+13 的设备上凭空差一天。
        /// </summary>
        private static int AgeInDays(long createUnixSec, DateTime utcNow)
        {
            if (createUnixSec < 0 || createUnixSec > MaxUnixSec)
            {
                return 0;
            }

            DateTime created = UnixEpochUtc.AddSeconds(createUnixSec);
            return (utcNow.Date - created.Date).Days;
        }

        private static List<string> NamesOf(IReadOnlyList<LogOutboxFile> files)
        {
            var names = new List<string>(files.Count);
            for (int i = 0; i < files.Count; i++)
            {
                names.Add(files[i].Name);
            }

            return names;
        }

        /// <summary>「文件名 → 字节数」查表。删除计划只有名字，报告要说清释放了多少。</summary>
        private static Dictionary<string, long> SizeMap(IReadOnlyList<LogOutboxFile> files)
        {
            var map = new Dictionary<string, long>(files.Count, StringComparer.Ordinal);
            for (int i = 0; i < files.Count; i++)
            {
                map[files[i].Name] = files[i].Bytes;
            }

            return map;
        }

        private static long SizeOf(Dictionary<string, long> sizes, string name)
        {
            long bytes;
            return sizes.TryGetValue(name, out bytes) ? bytes : 0;
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  诊断日志（全局硬约束 6）
        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 启动清理的结果。什么都没发生就不吭声 —— 绝大多数启动都属于这种，
        /// 每次都刷一行只会淹没别的信息。
        /// </summary>
        private void ReportStartup(int deleted, long freed, int tmpDeleted, int tmpSkipped)
        {
            if (deleted == 0 && tmpDeleted == 0 && tmpSkipped == 0)
            {
                return;
            }

            LogSystem.InternalInfo(
                "outbox startup: deleted " + deleted + " batch(es), " + (freed / 1024)
                + " KB freed, " + tmpDeleted + " stale tmp removed, " + tmpSkipped
                + " tmp kept (another instance may be writing); " + Count + " pending, "
                + (Bytes / 1024) + " KB");
        }

        /// <summary>
        /// 容量淘汰 = **真的把日志丢了**，必须报。
        /// <para>
        /// 但一旦装满，之后每 3 秒都会淘汰一次，所以叠一层限频：窗口复用
        /// <see cref="LogConfig.SuppressNoticeSec"/>（票 05 的裁定：不为它新增参数，
        /// 参数总表是唯一事实源），间隔用**单调时钟**（全局硬约束 2）。
        /// 被压住的那些**累加**到下一条里报出去 —— 报小了比不报更坏。
        /// </para>
        /// </summary>
        private void ReportEviction(int evicted, long freed)
        {
            if (evicted <= 0)
            {
                return;
            }

            int total;
            long totalBytes;

            lock (m_Lock)
            {
                m_PendingEvicted += evicted;
                m_PendingEvictedBytes += freed;

                long now = LogSystem.UptimeMs;
                long window = m_Config.SuppressNoticeSec * 1000L;
                if (m_EvictNoticeSeen && now - m_EvictNoticeUptimeMs < window)
                {
                    return;
                }

                m_EvictNoticeSeen = true;
                m_EvictNoticeUptimeMs = now;
                total = m_PendingEvicted;
                totalBytes = m_PendingEvictedBytes;
                m_PendingEvicted = 0;
                m_PendingEvictedBytes = 0;
            }

            LogSystem.InternalError(
                "outbox dropped " + total + " batch(es), " + (totalBytes / 1024)
                + " KB (over OutboxMaxBytes, oldest first)");
        }

        /// <summary>
        /// 写盘失败只报一次。没有这个闩，一次"磁盘满"会变成每 3 秒一条的错误刷屏，
        /// 而这些行自己是 Internal 日志、bypass 限流，谁也拦不住它们。
        /// </summary>
        private void ReportWriteFailure(string name, Exception e)
        {
            if (m_WriteFailureReported)
            {
                return;
            }

            m_WriteFailureReported = true;
            LogSystem.InternalError("outbox write failed (" + name + "): " + e.Message);
        }
    }
}
