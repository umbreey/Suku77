using System;
using System.Text;
using UnityEngine;

namespace Umbreey
{
    /// <summary>
    /// 一条日志的完整语义单元 —— 产生时刻、等级、正文、可选堆栈。
    /// 它是**采集之后、落盘和上传之前的唯一中间形态**（术语见 CONTEXT.md「日志记录」）。
    /// <para>
    /// 字段布局是冻结契约 C1（specs/0000-index.md），不得单方面增删改。
    /// </para>
    /// <para>
    /// 本类型会被打包线程读取，因此**不得引入任何需要调用 Unity API 才能求值的成员**
    /// （全局硬约束 1）。<see cref="LogType"/> 只是一个枚举，取值不涉及引擎调用，后台线程安全。
    /// </para>
    /// </summary>
    internal struct LogRecord
    {
        /// <summary>
        /// 日志产生时刻，Unix 毫秒。**在采集回调里取一次 DateTime.UtcNow 换算**（ADR-0002）。
        /// 这是它唯一能被称为"日志产生时刻"的位置，也是第三方 SDK 日志能拿到正确时间的唯一途径。
        /// 它是设备原始时间，**绝不能用时钟校正后的值**（全局硬约束 5）。
        /// </summary>
        public long TsUnixMs;

        /// <summary>UnityEngine.LogType 原值，不做映射。</summary>
        public LogType Type;

        /// <summary>
        /// 原始 logString。**含 Log.cs 拼的本地时间前缀，不剥离** ——
        /// 第三方 SDK 的日志没有这个前缀，无法可靠区分（ADR-0002）。
        /// 敏感哨兵前缀是唯一被剥掉的东西（见 <see cref="Sensitive"/>）。
        /// </summary>
        public string Msg;

        /// <summary>stackTrace，可能为空。</summary>
        public string Stack;

        /// <summary>
        /// 估算字节数，**必须包含 <see cref="Stack"/> 的长度**；宁可高估。
        /// 漏算堆栈会让异常刷屏时的会话计数严重低估，而本地卷是按 msg + stack 实际写的，
        /// "单卷不跨会话"这条保证就依赖它。
        /// <para>
        /// 契约 C1 写的是"估算"，<see cref="EstimateSize"/> 给的是**精确的 UTF-8 字节数** ——
        /// 精确同样满足"不得低估"，且与本地卷用的是同一把尺子。理由见该方法。
        /// </para>
        /// </summary>
        public int Size;

        /// <summary>
        /// 采集那一刻的上下文版本号，见契约 C2 与 <see cref="LogContextSnapshot.Version"/>。
        /// <para>
        /// 它解决的是**身份错归属**：一条登录前产生的日志若在登录后才被打包，
        /// 按"打包时取快照"会被打上新的 uid，凭空归到某个玩家名下。
        /// 因此一个批次只能包含 CtxVer 相同的记录，版本变化处必须切批（切批由 S3 负责）。
        /// </para>
        /// </summary>
        public int CtxVer;

        /// <summary>
        /// 日志系统自产的诊断日志（写盘失败、限流触顶、丢弃计数、上传结果与 code、
        /// 地址切换、offset 触发）。见全局硬约束 6：走 Debug.Log 保证 logcat 可见，
        /// 但 **bypass 限流计数**，且处理它的过程中不得再产生新的内部日志（重入守卫）。
        /// </summary>
        public bool Internal;

        /// <summary>
        /// 含敏感信息，**只落本地卷、不进批次累加器**（全局硬约束 7）。
        /// <para>
        /// 标记描述的是**内容性质，不是处理方式** —— 策略点只有打包线程里那一行，
        /// 将来改成"脱敏后上传"或"上传到受限 topic"时 call site 一行都不用动。
        /// </para>
        /// </summary>
        public bool Sensitive;

        // ────────────────────────────────────────────────────────────────────────────────
        //  采集（票 03）。以下全部是**纯函数**：不碰 Unity API、不读全局状态、无副作用，
        //  因此既能在采集回调的任意线程上调用（全局硬约束 1），也能在 EditMode 里直接测。
        //  它们只给 LogRecord 加静态方法，**不动契约 C1 的字段布局**。
        // ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 敏感哨兵的扫描窗口：只在正文的**前 48 个字符**里找 <see cref="LogSystem.SensitiveTag"/>。
        ///
        /// <para>
        /// 窗口的下界是外部事实夹出来的：Log.cs 的 Content() 会在正文前拼一个
        /// **固定 20 字符**的本地时间前缀（"09/08 15:30:45.123: "），而哨兵本身 12 字符 ——
        /// 20 + 12 = 32 是"带前缀时哨兵完整落在窗口内"所需的**最小值**。
        /// 窗口存在的意义是别让正文里偶然出现的同名字符串误伤（见票 03 的第 4 条测试）。
        /// </para>
        ///
        /// <para>
        /// **为什么是 48 而不是 32。** 32 是零余量：哨兵恰好占 [20, 32)，贴着窗口右边缘命中。
        /// 于是任何人给 Log.cs 的 Content() 前缀加点东西（一个等级标签、给时间戳补上年份），
        /// 已经打好标记的凭据日志就会**静默失去保护** —— 编译过、跑得动、没有任何报错，
        /// 只是 token 从此照常进批次、照常上传到 CLS。这是典型的 fail-open 静默失效。
        /// </para>
        /// <para>
        /// 放宽到 48 留出 16 字符余量，把"将来给前缀加点东西"这类改动提前罩住。
        /// 代价是误伤面略微变大：正文前 48 字符里**恰好**出现 `[#sensitive]` 字样的普通日志
        /// （比如某段日志正在打印"我们用 [#sensitive] 这个前缀"）会被当成敏感。但两侧的代价不对称：
        /// **误伤 = 少上传一条日志（fail-closed，本地卷里仍然有，照样能查）**，
        /// 而**漏判 = 凭据外泄到 CLS（fail-open，且无声无息）**。
        /// 安全机制必须偏向前者，所以宁可窗口宽一点。
        /// </para>
        ///
        /// <para>
        /// ⚠️ 它不在参数总表里，因为它不是可调策略参数 —— 它与 Log.cs 的前缀长度耦合，
        /// 是实现常量。所以写成常量而不是 LogConfig 字段，**也不要挪进 LogConfig**。
        /// 调它的唯一理由是前缀或哨兵的长度变了；LogCaptureTests 里的断言一律从这个常量算出来，
        /// 改值时测试会自动跟上。
        /// </para>
        /// </summary>
        internal const int SensitiveScanChars = 48;

        /// <summary>
        /// 每条记录写进卷时除正文外的固定开销上界：最多两个换行（正文行 + 堆栈行）。
        /// 留到 8 是为了把"将来加个分隔符"这类小改动也罩住 —— 这一项宁可高估。
        /// </summary>
        private const int RecordOverheadBytes = 8;

        /// <summary>
        /// 把一次 <c>logMessageReceivedThreaded</c> 回调变成一条日志记录。
        /// 术语见 CONTEXT.md「采集」：这里是日志记录唯一的诞生地。
        ///
        /// <para>
        /// ⚠️ 由**任意线程**调用（TCPClient 就在 ThreadPool 线程上打日志），因此本方法
        /// 不得有任何共享可变状态。时间戳由调用方在回调里取一次 DateTime.UtcNow 后传进来
        /// （ADR-0002：那是它唯一能被称为"日志产生时刻"的位置）。
        /// </para>
        /// </summary>
        /// <param name="tsUnixMs">日志产生时刻，Unix 毫秒。设备原始时间，不做时钟校正（全局硬约束 5）。</param>
        /// <param name="type">UnityEngine.LogType 原值，不做映射（契约 C1）。</param>
        /// <param name="logString">原始正文。除敏感哨兵外**什么都不剥离**，Log.cs 的时间前缀保留。</param>
        /// <param name="stackTrace">堆栈，可能为空。</param>
        /// <param name="ctxVer">采集那一刻的上下文版本号（契约 C2），供 S3 切批。</param>
        /// <param name="isInternal">日志系统自产的诊断日志（全局硬约束 6）。</param>
        internal static LogRecord Capture(
            long tsUnixMs, LogType type, string logString, string stackTrace, int ctxVer, bool isInternal)
        {
            string msg;
            bool sensitive = TryStripSensitiveTag(logString, out msg);
            string stack = stackTrace ?? "";

            return new LogRecord
            {
                TsUnixMs = tsUnixMs,
                Type = type,
                Msg = msg,
                Stack = stack,
                Size = EstimateSize(msg, stack),
                CtxVer = ctxVer,
                Internal = isInternal,
                Sensitive = sensitive,
            };
        }

        /// <summary>
        /// 识别并剥离敏感哨兵（全局硬约束 7）。命中返回 true，
        /// <paramref name="stripped"/> 是**剥掉哨兵后**的正文；未命中返回 false，原样返回。
        ///
        /// <para>
        /// ⚠️ **绝不能改成 StartsWith。** Log.cs 会在正文前拼 20 字符的时间戳，而第三方 SDK
        /// 的日志没有它 —— 哨兵的位置不固定。用 StartsWith 的话这个机制会**完全失效，
        /// 且没有任何报错**：凭据会照常进批次、照常上传到 CLS。
        /// 票 03 里那 5 条测试就是专门钉这件事的。
        /// </para>
        ///
        /// <para>
        /// 剥离用 <c>Remove(idx, len)</c> 而不是 <c>Substring</c> —— 哨兵前面那段
        /// （时间前缀）是要保留的，Substring 会把它一起切掉。
        /// </para>
        ///
        /// <para>
        /// 窗口语义是"哨兵**完整落在**前 <see cref="SensitiveScanChars"/> 个字符内"：
        /// String.IndexOf(value, 0, count, Ordinal) 要求匹配整体位于 [0, count) 之内。
        /// 带 20 字符时间前缀时哨兵占 [20, 32)，窗口 48 之内还留着 16 字符余量 ——
        /// 那 16 字符是**故意**留的，理由见 <see cref="SensitiveScanChars"/>。
        /// </para>
        /// </summary>
        internal static bool TryStripSensitiveTag(string logString, out string stripped)
        {
            string msg = logString ?? "";
            string tag = LogSystem.SensitiveTag;

            int window = msg.Length < SensitiveScanChars ? msg.Length : SensitiveScanChars;
            if (window < tag.Length)
            {
                stripped = msg;
                return false;
            }

            int idx = msg.IndexOf(tag, 0, window, StringComparison.Ordinal);
            if (idx < 0)
            {
                stripped = msg;
                return false;
            }

            stripped = msg.Remove(idx, tag.Length);
            return true;
        }

        /// <summary>
        /// 一条记录的字节数。**必须包含堆栈的长度**（契约 C1）——
        /// 漏算堆栈会让异常刷屏时的会话计数严重低估。
        ///
        /// <para>
        /// 取的是**精确的 UTF-8 字节数**，不是上界估算。理由是"同一把尺子"：
        /// 参数总表把 SessionLimitBytes 与 VolumeMaxBytes 对齐到同一个数（20MB），
        /// 用这条对齐换来"一次会话永不跨卷"，而卷那边累加的是
        /// <see cref="LogVolume"/> 真实编码后写进去的字节数。两边必须同量纲，
        /// 否则"对齐"只是名义上的：按 3×字符数 的上界算，纯 ASCII 日志下两把尺子差 3 倍，
        /// 会话会在卷才用掉三分之一时就停止记录 —— 那不是保守，是把设计意图打了折。
        /// </para>
        /// <para>
        /// 契约 C1 那句"宁可高估"的本意是**不得低估**（所以必须含 Stack）；
        /// 精确值同样满足不低估，而且更好。
        /// </para>
        /// <para>
        /// 成本可忽略：GetByteCount 是一次 O(n) 扫描，与同一条记录上已有的
        /// 前 <see cref="SensitiveScanChars"/> 字符 IndexOf 同量级，
        /// 而这条记录本来就要被 UTF-8 编码写盘。
        /// </para>
        /// </summary>
        internal static int EstimateSize(string msg, string stack)
        {
            // 用 long 累加再夹取：爆栈时单条 stack 可能极大，两项相加理论上会溢出 int。
            long bytes = (long)Utf8ByteCount(msg) + Utf8ByteCount(stack) + RecordOverheadBytes;
            return bytes > int.MaxValue ? int.MaxValue : (int)bytes;
        }

        /// <summary>null 与空串都算 0 字节 —— 采集回调里不能因为一个 null 就抛。</summary>
        private static int Utf8ByteCount(string value)
        {
            return string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);
        }
    }

    /// <summary>
    /// 运行中变化的业务值的**不可变快照**，用于跨线程传递（冻结契约 C2）。
    /// <para>
    /// 机制：SetXxx 在**主线程**构造新实例、递增 <see cref="Version"/>、并 volatile 换引用；
    /// 打包线程只读。**禁止**让打包线程回调业务侧的 getter —— 那既是数据竞争，
    /// 也可能在后台线程上间接触发 Unity API（全局硬约束 1）。
    /// </para>
    /// <para>
    /// 全部字段 readonly，实例一旦构造就不再改变；"变化"一律表现为换一个新实例。
    /// </para>
    /// <para>
    /// 字符串字段**未取到值时是空串而不是 null**，由构造函数统一兜底 ——
    /// 契约 C2 明确要求（"未登录时为 ''，不是 null"），批头序列化侧才不必逐个判空。
    /// </para>
    /// </summary>
    internal sealed class LogContextSnapshot
    {
        /// <summary>
        /// 单调递增的版本号。采集时记进 <see cref="LogRecord.CtxVer"/>，供 S3 切批。
        /// <para>0 表示"还没有任何 SetXxx 发生过"，即 <see cref="Empty"/> 这个初始快照。</para>
        /// </summary>
        public readonly int Version;

        /// <summary>玩家标识。未登录时为空串。退出登录回到登录界面时必须清回空串并递增版本。</summary>
        public readonly string Uid;

        /// <summary>环境。登录前为空串。<see cref="Uid"/> 与它必须**原子地一次更新**（见 C2）。</summary>
        public readonly string Env;

        /// <summary>资源版本。LoadBundleMap 之前为空串，热更完成后会变。</summary>
        public readonly string ResVersion;

        /// <summary>渠道。</summary>
        public readonly string Channel;

        /// <summary>Application.version 原值 —— **不是 App.Version**（后者把 '.' 换成了 '_'）。</summary>
        public readonly string AppVersion;

        /// <summary>android / ios / windows / mac / web。</summary>
        public readonly string Platform;

        /// <summary>DeviceInfo.model。取值走 JNI，**必须在主线程预取**后存进快照。</summary>
        public readonly string Device;

        /// <summary>DeviceInfo.gpu。同上，主线程预取。</summary>
        public readonly string Gpu;

        /// <summary>DeviceInfo.system。同上，主线程预取。</summary>
        public readonly string System;

        /// <summary>设备维度的持久化标识，由日志系统自己生成并落盘。未登录阶段唯一的检索锚点。</summary>
        public readonly string ClientId;

        /// <summary>本次会话的追踪标识。</summary>
        public readonly string TraceId;

        /// <summary>
        /// <see cref="Version"/> 为 0 的初始快照：进程刚起来、任何业务值都还没取到时用它，
        /// 这样"当前上下文"永远不为 null，采集端不必判空。
        /// </summary>
        internal static readonly LogContextSnapshot Empty = new LogContextSnapshot(
            0, null, null, null, null, null, null, null, null, null, null, null);

        internal LogContextSnapshot(
            int version,
            string uid,
            string env,
            string resVersion,
            string channel,
            string appVersion,
            string platform,
            string device,
            string gpu,
            string system,
            string clientId,
            string traceId)
        {
            Version = version;
            Uid = NotNull(uid);
            Env = NotNull(env);
            ResVersion = NotNull(resVersion);
            Channel = NotNull(channel);
            AppVersion = NotNull(appVersion);
            Platform = NotNull(platform);
            Device = NotNull(device);
            Gpu = NotNull(gpu);
            System = NotNull(system);
            ClientId = NotNull(clientId);
            TraceId = NotNull(traceId);
        }

        private static string NotNull(string value)
        {
            return value ?? "";
        }
    }
}
