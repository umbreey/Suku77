using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

#if !UNITY_WEBGL
using System.Net.Http;
#endif

namespace Umbreey
{
    // ════════════════════════════════════════════════════════════════════════════════════
    //  S4 · X-Timestamp 的独立时钟（票 10 / specs 0004「时钟」/ ADR-0002 末段 / 协议 §3.7 §4.3）
    //
    //  ⚠️ 本文件存在的全部理由是一句话：**X-Timestamp 不能建立在 TimeHelper 之上。**
    //
    //  早期方案是 `TimeHelper.UtcTimestamp + offset`，它有一个**必然发生**的 bug：
    //
    //    1. 设备时钟慢 1 小时 → 登录前 code=5 → 算出 offset=+3600 → 请求恢复正常
    //    2. 登录成功，TimeHelper 被服务器时间同步，**基准本身变正确了**
    //    3. offset 仍在叠加 → 请求反而**快** 1 小时 → 再次超窗失败
    //    4. 「只校正首次 code=5」的策略让它**再也无法恢复**
    //
    //  所以 LogClock 自成一体：「/v1/time 锚点 + 单调计时」，**全文一处都不读 TimeHelper**
    //  （也不读 LogSystem 里任何墙上时钟的派生值）。这不是风格问题 ——
    //  只要读了它，上面那四步就会原样重现，而表现是"上报先好了、登录后又全挂"。
    //
    //  ⚠️ 另一条同等重要、且方向相反的硬约束（全局硬约束 5 / ADR-0002 末段）：
    //  **logs[].ts 绝不使用本时钟**，永远是采集回调里 DateTime.UtcNow 的原始值。
    //  X-Timestamp 一旦被校正就永远落在服务端 ±300 秒窗口内，服务端再也无法从请求头
    //  识别时钟异常的设备，此时 `rt - ts` 是仅剩的线索 —— 在客户端"顺手修正" ts
    //  会把这个证据一起抹掉。本文件因此**不提供任何毫秒接口**，从 API 形状上就用不到那里去。
    // ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>一次重锚尝试的结论。</summary>
    internal enum LogClockResync
    {
        /// <summary>本时钟不做校正（<see cref="LogUploadDeviceClock"/>）。什么也没发生。</summary>
        Disabled = 0,

        /// <summary>
        /// 最小间隔内的重复请求，**被限流挡掉**，锚点不变。
        /// <para>
        /// 这一档是防刷：服务端持续返回 <c>code=5</c> 时（比如它自己的时钟坏了），
        /// 每个批次的每次重试都会走到这里，不限流就会把 <c>/v1/time</c> 打成一条压测流量。
        /// </para>
        /// </summary>
        Throttled,

        /// <summary>拉取或解析失败，**锚点保持原样**（宁可继续用旧锚点，也不要拿垃圾值覆盖它）。</summary>
        Failed,

        /// <summary>
        /// 锚点已重设，<see cref="LogClock.Epoch"/> 已递增。
        /// ⚠️ 调用方必须据此**立刻作废所有缓存的签名与请求头**。
        /// </summary>
        Anchored,
    }

    /// <summary>
    /// <c>GET /v1/time</c>（协议 §3.7：**免签名、无请求头、不校验身份**）的执行者。
    ///
    /// <para>
    /// 抽成接口只为一件事：让重锚的**决策与状态机**（限流、锚点、Epoch）能在 EditMode 里
    /// 不碰网络地断言 —— 与票 09 把 <see cref="ILogUploadTransport"/> 抽出来同一个理由。
    /// </para>
    /// <para>⚠️ 实现方跑在上传线程上，不得调用任何 Unity API（全局硬约束 1），且**不得抛异常**。</para>
    /// </summary>
    internal interface ILogServerTimeSource
    {
        /// <returns>
        /// false = 没拿到可用的 <c>server_time</c>（网络失败、body 畸形、值不合法）。
        /// 此时 <paramref name="serverUnixSec"/> 无意义。
        /// </returns>
        bool TryGetServerUnixSec(
            string timeUrl, CancellationToken token, out long serverUnixSec, out string error);
    }

#if !UNITY_WEBGL
    /// <summary>
    /// <c>GET /v1/time</c> 的真实实现。
    ///
    /// <para>
    /// ⚠️ **复用上传通道那个单例 <c>HttpClient</c>**（<see cref="LogHttpUploadTransport.Shared"/>）：
    /// 每次 new 一个会让每个请求各开一条连接、TIME_WAIT 累积到 socket 耗尽。
    /// 校时请求本来就稀疏（最小间隔限流），更没有理由为它单开一个客户端。
    /// </para>
    /// <para>
    /// 超时与取消两道都在：<c>HttpClient.Timeout</c>（<see cref="LogConfig.HttpTimeoutMs"/>）
    /// 与调用方的 <see cref="CancellationToken"/> —— 后者让
    /// <see cref="ILogUploader.Stop"/> 不会被一次在飞的校时请求拖住。
    /// </para>
    /// </summary>
    internal sealed class LogHttpServerTimeSource : ILogServerTimeSource
    {
        private readonly int m_TimeoutMs;

        internal LogHttpServerTimeSource(int timeoutMs)
        {
            m_TimeoutMs = timeoutMs > 0 ? timeoutMs : new LogConfig().HttpTimeoutMs;
        }

        public bool TryGetServerUnixSec(
            string timeUrl, CancellationToken token, out long serverUnixSec, out string error)
        {
            serverUnixSec = 0L;

            if (string.IsNullOrEmpty(timeUrl))
            {
                error = "no address";
                return false;
            }

            try
            {
                // 免签名、无自定义头（协议 §3.7）——  这也正是它在 code=5 时仍然可用的原因：
                // 签名要带 X-Timestamp，而此刻我们恰好不知道正确的时间。
                // 同步等待：本方法跑在我们自己的后台上传线程上，没有 Unity 的同步上下文，
                // 不存在死锁风险（与票 09 的传输层同一个理由）。
                using (HttpResponseMessage response = LogHttpUploadTransport.Shared(m_TimeoutMs)
                    .GetAsync(timeUrl, token).GetAwaiter().GetResult())
                {
                    string body = response.Content != null
                        ? response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                        : "";

                    // ⚠️ 同样**不看 HTTP 状态码**（协议 §3.6：一律 200，结论在 body 里）。
                    if (LogClock.TryParseServerTime(body, out serverUnixSec))
                    {
                        error = null;
                        return true;
                    }

                    error = "unusable /v1/time body (http " + response.StatusCode.ToString("D") + ")";
                    return false;
                }
            }
            catch (Exception e)
            {
                // 连接失败、DNS、TLS、超时、Stop 取消 —— 一律"没拿到"，绝不抛。
                Exception inner = e;
                while (inner.InnerException != null)
                {
                    inner = inner.InnerException;
                }

                error = inner.GetType().Name + ": " + inner.Message;
                return false;
            }
        }
    }
#endif

    /// <summary>
    /// <c>X-Timestamp</c> 的时钟（票 10）。取代票 09 的半成品 <see cref="LogUploadDeviceClock"/>。
    ///
    /// <para>状态只有一个**可选锚点**：<c>(serverUnixSec, 锚点那一刻的单调时刻)</c>。</para>
    /// <list type="bullet">
    ///   <item><b>有锚点</b> → <c>serverUnixSec + 单调经过秒数</c></item>
    ///   <item><b>无锚点</b> → <c>DateTime.UtcNow</c> 的 Unix 秒（协议 §4.3 明确允许"什么都不做"）</item>
    /// </list>
    ///
    /// <para>
    /// ⚠️ **经过时间一律用单调时钟**（全局硬约束 2）：锚点建立之后，玩家改系统时间、
    /// 系统 NTP 校准、TimeHelper 登录同步**都不会影响** <see cref="NowUnixSec()"/>。
    /// 用墙上时钟算"经过"等于把刚校正好的时间又交还给那个不准的钟。
    /// </para>
    /// <para>
    /// ⚠️ 重锚**不是只做一次**：每次 <c>code=5</c> 都可以重设（票 10）。
    /// "只校正首次"是早期方案无法恢复的直接原因 —— 服务端换机、跨天漂移、
    /// 玩家中途再改一次时间，都需要第二次校正。
    /// 代价用**最小间隔限流**兜住，见 <see cref="MinResyncIntervalMs"/>。
    /// </para>
    /// <para>
    /// 线程：<see cref="NowUnixSec()"/> / <see cref="OnTimestampRejected"/> 由上传线程调用，
    /// 诊断属性可能被主线程读。锚点是三个字段的组合、必须整体一致，故整体加锁；
    /// <see cref="Epoch"/> 单独做成 volatile 让上传循环每轮无锁比对。
    /// </para>
    /// </summary>
    internal sealed class LogClock : ILogUploadClock
    {
        /// <summary>协议 §3.6 的 <c>server_time</c> 字段名。</summary>
        private const string ServerTimeField = "server_time";

        /// <summary>协议 §3.6 的结果码字段名。</summary>
        private const string CodeField = "code";

        private static readonly DateTime UnixEpochUtc =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly LogConfig m_Config;
        private readonly ILogServerTimeSource m_Source;

        /// <summary>
        /// 设备墙上时钟（Unix 秒）。**测试注入用** —— 真实路径是 <c>DateTime.UtcNow</c>。
        /// <para>
        /// ⚠️ 它是"无锚点时的回落"，不是 <see cref="NowUnixSec()"/> 的基准：
        /// 锚点一旦建立，这个函数的值再怎么跳都不影响 <see cref="NowUnixSec()"/>。
        /// 单元测试正是靠"让它跳一小时"来钉这件事。
        /// </para>
        /// </summary>
        private readonly Func<long> m_DeviceUnixSec;

        private readonly object m_Lock = new object();

        private bool m_HasAnchor;
        private long m_AnchorServerUnixSec;
        private long m_AnchorUptimeMs;

        /// <summary>最近一次锚点相对设备墙上时钟的偏差（秒），**纯诊断**。</summary>
        private long m_SkewSec;

        /// <summary>
        /// 已经尝试过重锚。⚠️ 单调时钟的 0 时刻是合法值，拿"上次时刻 == 0"当"没试过"
        /// 会让进程刚起来的头一分钟里第一次重锚被莫名其妙地吃掉（与 S1 的限频闸同一个坑）。
        /// </summary>
        private bool m_Attempted;

        private long m_LastAttemptUptimeMs;

        private volatile int m_Epoch;

        /// <summary>最近一次重锚失败的原因，**只进诊断日志**，不参与任何判定。</summary>
        private volatile string m_LastError;

        internal LogClock(LogConfig cfg, ILogServerTimeSource source)
            : this(cfg, source, null)
        {
        }

        internal LogClock(LogConfig cfg, ILogServerTimeSource source, Func<long> deviceUnixSec)
        {
            m_Config = cfg ?? new LogConfig();
            m_Source = source;
            m_DeviceUnixSec = deviceUnixSec ?? DeviceUnixSec;
        }

        /// <summary>
        /// 两次重锚之间的最小间隔。
        ///
        /// <para>
        /// ⚠️ **复用 <see cref="LogConfig.SuppressNoticeSec"/>（60s）而不是新增一个字段** ——
        /// 参数总表（specs/0000-index.md）是唯一事实源，而它里面没有"重锚最小间隔"这一项；
        /// 新增字段等于改唯一事实源，那是契约变更、不能在票里顺手做。
        /// 票 05 对"内部事件限频"做过同样的裁定，票 09 的 <see cref="LogUploadNoticeGate"/>
        /// 沿用了它，这里是第三处：三者的语义一致（**同一件自我保护动作之间至少隔这么久**），
        /// 取值也都正好是票 10 要求的 60 秒。
        /// </para>
        /// </summary>
        internal long MinResyncIntervalMs
        {
            get { return m_Config.SuppressNoticeSec * 1000L; }
        }

        /// <summary>已经有服务器时间锚点。诊断与测试用。</summary>
        internal bool HasAnchor
        {
            get { lock (m_Lock) { return m_HasAnchor; } }
        }

        /// <summary>
        /// 时钟基准的版本号（契约无关，是票 09 留给上传循环的通知机制）。
        /// **每次锚点变化就递增**，上传循环每轮比对它，一变就作废所有缓存的签名与请求头。
        /// </summary>
        public int Epoch
        {
            get { return m_Epoch; }
        }

        /// <summary>
        /// 当前 Unix **秒**（协议 §3.2：不是毫秒）。上传线程调用。
        /// <para>⚠️ 只给 <c>X-Timestamp</c> 用。<c>logs[].ts</c> 绝不许经过这里。</para>
        /// </summary>
        public long NowUnixSec()
        {
            return NowUnixSec(LogSystem.UptimeMs);
        }

        /// <summary>
        /// 注入单调时刻的版本，纯函数式，供 EditMode 断言。
        /// <para>
        /// 有锚点时的公式里**只有单调量**：<c>锚点服务器秒 + (now − 锚点单调时刻)/1000</c>。
        /// 墙上时钟（以及 TimeHelper 的任何同步跳变）在这条路径上根本不出现。
        /// </para>
        /// </summary>
        internal long NowUnixSec(long nowMs)
        {
            lock (m_Lock)
            {
                if (!m_HasAnchor)
                {
                    return m_DeviceUnixSec();
                }

                long elapsedMs = nowMs - m_AnchorUptimeMs;
                if (elapsedMs < 0L)
                {
                    // 单调时钟不该倒流。真倒流了就用锚点本身，不要算出一个比锚点还早的时间。
                    elapsedMs = 0L;
                }

                return m_AnchorServerUnixSec + elapsedMs / 1000L;
            }
        }

        /// <summary>
        /// 收到 <c>code=5</c>（时间戳超出 ±300 秒窗口，协议 §3.6）时的重锚机会。
        ///
        /// <para>
        /// ⚠️ 本方法**不改变重试行为**：<c>code=5</c> 和其它非 0 码一样走普通退避
        /// （协议 §3.6 / specs/0004 末段明确要求不给它写特殊分支）。
        /// 这里加的是一个**副作用**——顺手把时钟校准 ——  而不是一条新的失败分支。
        /// </para>
        /// <para>顺序刻意是"先记尝试时刻，再发请求"：请求失败也要占用这一次配额，
        /// 否则服务端 502 时每次 <c>code=5</c> 都会重试一发校时，限流就形同虚设。</para>
        /// </summary>
        /// <param name="timeUrl">当前地址上的 <c>/v1/time</c>（由 <see cref="ILogUploadAddresses.CurrentTimeUrl"/> 给出）。</param>
        /// <param name="nowMs">单调时刻（<see cref="LogSystem.UptimeMs"/>）。</param>
        public LogClockResync OnTimestampRejected(string timeUrl, long nowMs, CancellationToken token)
        {
            if (m_Source == null)
            {
                return LogClockResync.Disabled;
            }

            lock (m_Lock)
            {
                if (m_Attempted && nowMs - m_LastAttemptUptimeMs < MinResyncIntervalMs)
                {
                    return LogClockResync.Throttled;
                }

                m_Attempted = true;
                m_LastAttemptUptimeMs = nowMs;
            }

            long serverUnixSec;
            string error;
            if (!m_Source.TryGetServerUnixSec(timeUrl, token, out serverUnixSec, out error))
            {
                m_LastError = error;
                return LogClockResync.Failed;
            }

            m_LastError = null;
            SetAnchor(serverUnixSec, nowMs);
            return LogClockResync.Anchored;
        }

        /// <summary>最近一次重锚失败的原因，诊断用。</summary>
        internal string LastError
        {
            get { return m_LastError; }
        }

        /// <summary>
        /// 重设锚点并**递增 <see cref="Epoch"/>**。
        /// <para>
        /// ⚠️ 递增 Epoch 是这里唯一不能省的一步：上传循环靠它作废所有缓存的签名与请求头。
        /// 不作废就会带着校正前的旧签名一路重试到预算耗尽（旧时间戳必然继续落在窗口外）。
        /// </para>
        /// </summary>
        internal void SetAnchor(long serverUnixSec, long nowMs)
        {
            lock (m_Lock)
            {
                m_AnchorServerUnixSec = serverUnixSec;
                m_AnchorUptimeMs = nowMs;
                m_HasAnchor = true;

                // 偏差按"锚点建立那一刻的设备时间"算，纯诊断：真机上这一个数就能回答
                // "这台设备的钟差多少"，而那正是 code=5 排查要的答案（协议 §7）。
                m_SkewSec = serverUnixSec - m_DeviceUnixSec();

                // volatile 写放在最后：上传线程看见新 Epoch 时，锚点三个字段已经就位。
                m_Epoch = m_Epoch + 1;
            }
        }

        /// <summary>诊断文案（logcat / CLS）。不含任何凭据。</summary>
        internal string Describe()
        {
            lock (m_Lock)
            {
                if (!m_HasAnchor)
                {
                    return "no anchor (using the device clock)";
                }

                return "anchored at server_time="
                    + m_AnchorServerUnixSec.ToString(CultureInfo.InvariantCulture)
                    + ", device clock is off by "
                    + m_SkewSec.ToString(CultureInfo.InvariantCulture)
                    + "s, epoch=" + m_Epoch.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// 解析 <c>GET /v1/time</c> 的响应（协议 §3.7：<c>{ "code": 0, "server_time": 1756881000 }</c>）。
        ///
        /// <para>
        /// ⚠️ **判定逐层收紧，任何一层不符就是"没拿到"** —— 与票 09 解析上报响应同一套做法，
        /// 而这里的风险更高：把一个垃圾值当成服务器时间会让**之后所有请求**都带着错误的
        /// <c>X-Timestamp</c>，那比"这次没校时"坏得多。所以：
        /// </para>
        /// <list type="number">
        ///   <item>body 非空、是合法 JSON、顶层是对象</item>
        ///   <item>有 <c>server_time</c>，且**必须是 JSON 整数**（字符串 <c>"175..."</c>、浮点、null 全部拒掉）</item>
        ///   <item>值必须为正（<c>0</c> / 负数是明显的占位或写错，不是时间）</item>
        ///   <item>若带了 <c>code</c>，它必须是整数 <c>0</c>（服务端自己说这次不算成功，就不能用它的时间）</item>
        /// </list>
        /// <para>任何异常都不外抛：抛出去会掀翻上传线程，而那表现成"CLS 里再也没有日志"。</para>
        /// </summary>
        internal static bool TryParseServerTime(string body, out long serverUnixSec)
        {
            serverUnixSec = 0L;

            if (string.IsNullOrEmpty(body))
            {
                return false;
            }

            try
            {
                using (var reader = new JsonTextReader(new StringReader(body)))
                {
                    // 关掉日期识别：我们只要一个整数，不需要解析器去猜字符串是不是时间
                    // （那条路径在不同 culture 下行为不同）。
                    reader.DateParseHandling = DateParseHandling.None;

                    var obj = JToken.ReadFrom(reader) as JObject;
                    if (obj == null)
                    {
                        return false;
                    }

                    JToken code;
                    if (obj.TryGetValue(CodeField, StringComparison.Ordinal, out code) && code != null)
                    {
                        if (code.Type != JTokenType.Integer || code.Value<long>() != 0L)
                        {
                            return false;
                        }
                    }

                    JToken value;
                    if (!obj.TryGetValue(ServerTimeField, StringComparison.Ordinal, out value)
                        || value == null
                        || value.Type != JTokenType.Integer)
                    {
                        return false;
                    }

                    long seconds = value.Value<long>();
                    if (seconds <= 0L)
                    {
                        return false;
                    }

                    serverUnixSec = seconds;
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 设备墙上时钟的 Unix 秒。<c>DateTime.UtcNow</c> 是 BCL，后台线程安全（全局硬约束 1）。
        /// <para>
        /// ⚠️ 这里**刻意不用 <c>TimeHelper</c>**（它是本文件顶部那个 bug 的源头），
        /// 也不用 <c>LogSystem</c> 的任何时间 —— 无锚点时只有设备自己的时间可用，
        /// 那也正是协议 §4.3 允许的"什么都不做"。
        /// </para>
        /// </summary>
        private static long DeviceUnixSec()
        {
            return (long)(DateTime.UtcNow - UnixEpochUtc).TotalSeconds;
        }
    }
}
