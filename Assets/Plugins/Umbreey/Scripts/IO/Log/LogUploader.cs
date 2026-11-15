using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

#if !UNITY_WEBGL
using System.Net.Http;
using System.Net.Http.Headers;
#endif

namespace Umbreey
{
    // ════════════════════════════════════════════════════════════════════════════════════
    //  S4 · 上传通道（票 09 / specs 0004 / 协议 client-api-v1.md §3 §4 §5 §7）
    //
    //  本文件的组织方式是刻意的：**所有会导致"日志静默消失"的判定都是纯函数**
    //  （LogUploadResponse.Parse / LogUploadPolicy.* / LogUploadRetryBook），
    //  网络与线程只是它们的宿主。网络本身不测（价值低、脆弱），判定全测。
    //
    //  四个最容易做错、且错了就是**静默丢日志**的地方，逐个标了 ⚠️：
    //    1. 成功判定只看 body 的 code，**绝不看 HTTP 状态码**（协议一律回 200）
    //    2. 畸形 body 一律判失败 —— 反序列化器把缺失的 int 默认成 0，而 0 正好是成功码
    //    3. 重试预算是**一次连通性事件内**的，耗尽是**暂停不删除**
    //    4. 批次年龄用**单调时钟**，不用文件名里的 createUnixSec 减墙上时间
    // ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 一次发送尝试的结论。**三分而不是两分**（成功/失败）是硬要求：
    /// <list type="bullet">
    ///   <item><see cref="NetworkUnavailable"/> —— 连接层错误、TLS 失败、超时、Stop 取消，
    ///   **没拿到 HTTP 响应**。它会在 <see cref="ILogUploader.OnNetworkResumed"/> 时被重置预算。</item>
    ///   <item><see cref="ServerRejected"/> —— 拿到了响应但 <c>code != 0</c>（含 body 畸形）。
    ///   它和网络无关，**不因网络恢复而重置**，最终只靠 TTL 淘汰。</item>
    /// </list>
    /// 两者都走退避，但恢复语义完全不同（specs/0004「必须区分两类失败」）。
    /// 混成一类的后果：服务端配置错的批次会在每次网络抖动后重新烧一轮预算。
    /// </summary>
    internal enum LogUploadOutcome
    {
        /// <summary>body 里 <c>code == 0</c>。**唯一**的成功，成功才删本地文件。</summary>
        Success = 0,

        /// <summary>拿到响应，但 code 非 0 或 body 畸形。</summary>
        ServerRejected,

        /// <summary>没拿到 HTTP 响应。</summary>
        NetworkUnavailable,
    }

    /// <summary>
    /// 响应体的解析结果（协议 §3.6）。
    ///
    /// <para>
    /// ⚠️ **这个类型存在的唯一理由是挡住"缺失的 int 默认成 0"。**
    /// 直接 <c>JsonConvert.DeserializeObject&lt;T&gt;</c> 到一个带 <c>int code</c> 字段的类上，
    /// <c>{}</c>、<c>{"code":null}</c>、<c>{"code":"0"}</c>、空 body 全都会得到 <c>code == 0</c>,
    /// 而 0 正好是成功码 —— 于是**批次被判成功、本地文件被删掉、日志静默消失**。
    /// 所以这里把"有没有 code"与"code 是多少"拆成两件事，
    /// 并且**只承认 JSON 整数**（<see cref="JTokenType.Integer"/>）。
    /// </para>
    /// </summary>
    internal struct LogUploadResponse
    {
        /// <summary>协议 §3.6 的结果码字段名。</summary>
        private const string CodeField = "code";

        /// <summary>没有响应可解析时的占位值（连接层失败）。</summary>
        internal static readonly LogUploadResponse None = Malformed("no response");

        /// <summary>
        /// body 里**确实带了一个 JSON 整数形式的 code**。
        /// false 时 <see cref="Code"/> 无意义，且一律判失败。
        /// </summary>
        internal readonly bool HasCode;

        /// <summary>结果码。仅当 <see cref="HasCode"/> 为 true 时有意义。</summary>
        internal readonly long Code;

        /// <summary>
        /// 畸形原因，**只进诊断日志**，不参与任何判定。
        /// 真机上"上传全失败"时这一行是区分"服务端换了响应格式"与"网络被劫持返回了 HTML"的唯一线索。
        /// </summary>
        internal readonly string Malformation;

        private LogUploadResponse(bool hasCode, long code, string malformation)
        {
            HasCode = hasCode;
            Code = code;
            Malformation = malformation;
        }

        /// <summary>
        /// ⚠️ **成功的唯一定义**：body 里有一个 JSON 整数 code，且它等于 0。
        /// <para>
        /// HTTP 状态码**不出现在这里，也不出现在任何调用方**（协议 §3.6：所有响应一律 200，
        /// 按状态码判断会把每次失败都当成功然后删掉本地文件）。
        /// </para>
        /// </summary>
        internal bool IsSuccess
        {
            get { return HasCode && Code == 0L; }
        }

        /// <summary>
        /// 解析响应体。**任何异常都不外抛** —— 判定层抛异常会把上传线程掀翻，
        /// 而"上传线程死了"的表现是"CLS 里再也没有日志"，极难查。
        ///
        /// <para>判定逐层收紧，任何一层不符就是失败（= 保留本地文件，等下一轮）：</para>
        /// <list type="number">
        ///   <item>body 非空（空 body / 纯空白 → 失败）</item>
        ///   <item>是合法 JSON（不是 JSON → 失败）</item>
        ///   <item>顶层是对象（<c>0</c>、<c>[]</c>、<c>"ok"</c> 这种合法但不是对象的 → 失败）</item>
        ///   <item>有 <c>code</c> 字段（<c>{}</c> → 失败）</item>
        ///   <item><c>code</c> 是 **JSON 整数**（<c>null</c> / 字符串 <c>"0"</c> / 浮点 /
        ///   布尔 → 失败）</item>
        /// </list>
        /// <para>
        /// 第 5 条刻意不做"宽容转换"：<c>"0"</c> 能被 <c>Value&lt;long&gt;()</c> 转成 0，
        /// 但一个把 code 序列化成字符串的服务端说明协议对不上，此时**把批次留在本地**
        /// 才是安全方向。宁可多传一次（协议 §7 明确说重复是正常现象、服务端不去重），
        /// 也不能把没确认收下的日志删掉。
        /// </para>
        /// </summary>
        internal static LogUploadResponse Parse(string body)
        {
            if (string.IsNullOrEmpty(body))
            {
                return Malformed("empty body");
            }

            try
            {
                using (var reader = new JsonTextReader(new StringReader(body)))
                {
                    // 关掉日期识别：我们只关心一个整数，不需要让解析器去猜字符串是不是时间，
                    // 那条路径在不同 culture 下行为不同。
                    reader.DateParseHandling = DateParseHandling.None;

                    JToken root = JToken.ReadFrom(reader);

                    var obj = root as JObject;
                    if (obj == null)
                    {
                        return Malformed("body is not a JSON object");
                    }

                    JToken code;
                    if (!obj.TryGetValue(CodeField, StringComparison.Ordinal, out code) || code == null)
                    {
                        // ⚠️ 这一条就是"缺失的 int 默认成 0"那个坑的入口。
                        return Malformed("code field missing");
                    }

                    if (code.Type != JTokenType.Integer)
                    {
                        // null / 字符串 "0" / 浮点 / 布尔 / 对象 / 数组全在这里被挡住。
                        return Malformed("code is " + code.Type);
                    }

                    return new LogUploadResponse(true, code.Value<long>(), null);
                }
            }
            catch (Exception e)
            {
                // 不是 JSON、被网关换成了 HTML、截断的 body —— 一律失败，绝不抛。
                return Malformed("unparsable body: " + e.GetType().Name);
            }
        }

        private static LogUploadResponse Malformed(string reason)
        {
            return new LogUploadResponse(false, 0L, reason);
        }

        /// <summary>诊断文案。**不含 body 原文**：它可能很大，也可能是被劫持的整页 HTML。</summary>
        internal string Describe()
        {
            return HasCode
                ? "code=" + Code.ToString(CultureInfo.InvariantCulture)
                : "malformed body (" + (Malformation ?? "unknown") + ")";
        }
    }

    /// <summary>一次发送的签名计划（协议 §4.4「重试时的 X-Timestamp」）。</summary>
    internal enum LogUploadSignPlan
    {
        /// <summary>
        /// 原样重发：<c>X-Timestamp</c> 与签名都不变，窗口内重试**零计算**。
        /// </summary>
        Reuse = 0,

        /// <summary>
        /// 重新签名：**payload 一个字节都不动**，取当前时间做新的 <c>X-Timestamp</c>，
        /// 拿缓存的 sha256 摘要重算一次 HMAC。
        /// </summary>
        Resign,
    }

    /// <summary>
    /// 上传决策的**全部纯函数**。不碰网络、不碰文件、不读时钟（当前时刻与随机数由调用方注入），
    /// 因此每个分支都能在 EditMode 里摆出来直接断言 —— 与票 04/08 的保留策略同一套做法。
    /// </summary>
    internal static class LogUploadPolicy
    {
        /// <summary>抖动带宽的分子（与 <see cref="JitterDenominator"/> 配成 1/4 = 25%）。</summary>
        internal const int JitterNumerator = 1;

        /// <summary>抖动带宽的分母。</summary>
        internal const int JitterDenominator = 4;

        /// <summary>
        /// 指数移位的上限。<c>begin &lt;&lt; 40</c> 会溢出成负数，
        /// 而负的退避间隔意味着"立刻重试" —— 正是惊群本身。
        /// </summary>
        private const int MaxShift = 30;

        /// <summary>
        /// ⚠️ **把传输层结果翻译成结论。整个上传通道的成功判定只有这一处。**
        ///
        /// <para>
        /// <b>只看 body 的 <c>code</c>，绝不看 HTTP 状态码</b>（协议 §3.6：
        /// 所有响应一律 HTTP 200，业务结果在 body 里）。
        /// <see cref="LogUploadTransportResult.StatusCode"/> 只被诊断日志引用，
        /// 本方法一个字都不读它 —— 按状态码判断会把每一次失败都当成成功、
        /// 然后删掉本地文件，日志静默消失。
        /// </para>
        /// <para>
        /// 反过来也成立：**HTTP 500 且 body 是 <c>{"code":0}</c> 时判为成功**。
        /// 网关改写状态码、代理返回 5xx 但透传了 body 都会造成这种情形，
        /// 而服务端已经明确说"收下了"。
        /// </para>
        /// </summary>
        internal static LogUploadOutcome Classify(LogUploadTransportResult transport, out LogUploadResponse response)
        {
            if (!transport.GotResponse)
            {
                // 连接层错误 / TLS 失败 / 超时 / Stop 取消：**没拿到响应**。
                // 这一类（且只有这一类）会被 OnNetworkResumed 重置预算。
                response = LogUploadResponse.None;
                return LogUploadOutcome.NetworkUnavailable;
            }

            response = LogUploadResponse.Parse(transport.Body);

            // ⚠️ 不对 code 做任何分支处理（协议 §3.6 / specs/0004 末段）：
            // 非 0 一律进重试，code=5 也走普通退避 —— 它单独存在的意义纯粹是联调时
            // 能一眼区分"时钟不对"和"签名写错"，不是让调用方给它写特例。
            return response.IsSuccess ? LogUploadOutcome.Success : LogUploadOutcome.ServerRejected;
        }

        /// <summary>限流（协议 §3.6 <c>code=3</c>）。很可能是**单节点**限流。</summary>
        internal const long RateLimitedCode = 3L;

        /// <summary>过载或下游不可用（协议 §3.6 <c>code=4</c>）。字面就是**这台机器**不行了。</summary>
        internal const long OverloadedCode = 4L;

        /// <summary>内部错误（协议 §3.6 <c>code=9</c>）。可能是**这台**的问题。</summary>
        internal const long InternalErrorCode = 9L;

        /// <summary>
        /// ⚠️ **这次失败要不要换地址**（协议 §4.2 规则 2：「连不上**或请求失败**，换下一个」）。
        ///
        /// <para>
        /// 划分依据**不是"有没有拿到 HTTP 响应"**，而是**这次失败说明了什么**：
        /// </para>
        /// <list type="bullet">
        ///   <item><b>节点级失败</b>（这台服务不了我）→ <b>换地址</b>：没拿到响应，
        ///   或 <c>code ∈ {3, 4, 9}</c>（单节点限流 / 这台过载 / 这台内部错误）。</item>
        ///   <item><b>请求级失败</b>（我的请求本身不对）→ <b>不换</b>：
        ///   <c>code ∈ {1, 2, 5}</c>（报文超限 / 签名或 appId 不对 / 时间窗超限），
        ///   换哪台都是同一个结果。畸形 body 也归这一类 —— 它说明协议对不上，换址不解决。</item>
        ///   <item><b>未知 code</b> → <b>保守不换</b>。不认识的东西不要当成节点故障，
        ///   否则将来服务端加一个新错误码，客户端就会把整个地址列表轮一遍。</item>
        /// </list>
        /// <para>
        /// ⚠️ **<c>code=4</c> 是不能收窄的那个反例**：它字面意思就是「这个节点过载」。
        /// 按"拿到响应就说明这台是通的、所以不换"来实现，客户端会**永远钉在一台过载的节点上
        /// 退避到暂停**，而列表里下一台是健康的 —— 后来人别再把这里收窄回去。
        /// </para>
        /// <para>
        /// 原先担心的"反复横跳"在这个划分下不会发生：<c>code=1/2/5</c> 根本不触发切换；
        /// 而 <c>3/4/9</c> 触发切换恰恰是想要的行为。即便所有地址都在过载，
        /// 「全部失败回到第 0 个」加上退避预算仍然把它兜成有界的。
        /// </para>
        /// <para>
        /// ⚠️ <c>code=5</c> 在这里**只决定"不换地址"**；它另有一条正交的处理 ——
        /// 触发 <c>GET /v1/time</c> 重锚（见 <c>LogUploader.TryResyncClock</c>）。
        /// 两者都不是"给 code=5 写的重试特例"，重试一律走普通退避。
        /// </para>
        /// </summary>
        internal static bool ShouldSwitchAddress(LogUploadOutcome outcome, LogUploadResponse response)
        {
            if (outcome == LogUploadOutcome.NetworkUnavailable)
            {
                // 连接层错误 / TLS / 超时：最典型的节点级失败。
                return true;
            }

            if (outcome != LogUploadOutcome.ServerRejected)
            {
                return false;
            }

            if (!response.HasCode)
            {
                // 畸形 body：协议对不上，不是这台机器的问题。
                return false;
            }

            long code = response.Code;
            return code == RateLimitedCode
                || code == OverloadedCode
                || code == InternalErrorCode;
        }

        /// <summary>
        /// 退避间隔（协议 §4.4）。**纯函数**：重试次数 + 配置 + 一个 [0,1) 的抖动量 → 毫秒数。
        ///
        /// <para>
        /// 抖动带是 <c>[base, base × 1.25]</c>，其中 <c>base = BackoffBeginMs × 2^(n-1)</c>，
        /// **带的上沿封顶 <see cref="LogConfig.BackoffMaxMs"/>**。默认参数下的序列是
        /// 1~1.25s / 2~2.5s / 4~5s / 8~10s / 16~20s，五次合计约 31 秒
        /// —— 这正是"断网超过半分钟就必须靠暂停而不是删除来兜住"的由来。
        /// </para>
        /// <para>
        /// ⚠️ **jitter 是硬要求，不是优化**：开服、活动、服务器重启时海量客户端同时重试
        /// 会形成惊群。所以带宽**在封顶处也不能塌掉** —— 饱和时带是
        /// <c>[Max × 0.8, Max]</c>（下沿由上沿反推），而不是所有客户端都精确地等 60 秒。
        /// </para>
        /// <para>
        /// 抖动量由调用方注入（真实路径给 <see cref="Random.NextDouble"/>，测试给定值），
        /// 于是"带宽正确"与"确实在抖"这两件事可以分开断言。
        /// </para>
        /// </summary>
        /// <param name="attempt">这是本次连通性事件内的第几次失败（1 起）。</param>
        /// <param name="jitter">[0,1] 的抖动量，超出范围会被夹住。</param>
        /// <returns>
        /// false = **预算耗尽，放弃本轮**。调用方据此把批次置为"暂停"，
        /// ⚠️ **不是删除**（票 09「重试」节 / 协议 §4.4「重试预算的作用域」）。
        /// </returns>
        internal static bool TryBackoffDelayMs(int attempt, LogConfig cfg, double jitter, out int delayMs)
        {
            delayMs = 0;
            if (cfg == null)
            {
                return false;
            }

            if (attempt < 1)
            {
                attempt = 1;
            }

            if (attempt > cfg.RetryMax)
            {
                // 一次连通性事件内的预算用完了。批次留在 outbox 里等 OnNetworkResumed。
                return false;
            }

            long begin = cfg.BackoffBeginMs > 0 ? cfg.BackoffBeginMs : 1L;
            long ceiling = cfg.BackoffMaxMs > begin ? cfg.BackoffMaxMs : begin;

            int shift = attempt - 1;
            if (shift > MaxShift)
            {
                shift = MaxShift;
            }

            long baseMs = begin << shift;
            if (baseMs <= 0L || baseMs > ceiling)
            {
                baseMs = ceiling;
            }

            // 带的上沿：base × (1 + 1/4)，封顶 BackoffMaxMs。
            long top = baseMs + baseMs * JitterNumerator / JitterDenominator;
            if (top > ceiling)
            {
                top = ceiling;
            }

            // 带的下沿由上沿反推（top ÷ 1.25 == top − top/5），于是封顶时带宽不塌。
            long bottom = top - top * JitterNumerator / (JitterDenominator + JitterNumerator);
            if (bottom < begin)
            {
                bottom = begin;
            }

            if (bottom > top)
            {
                bottom = top;
            }

            if (jitter < 0d)
            {
                jitter = 0d;
            }
            else if (jitter > 1d)
            {
                jitter = 1d;
            }

            delayMs = (int)(bottom + (long)((top - bottom) * jitter));
            return true;
        }

        /// <summary>
        /// 重签名判定（协议 §4.4 / 票 09「重签名窗口」）。**纯函数**。
        ///
        /// <para>
        /// ⚠️ <paramref name="ageMs"/> 必须是**单调时钟**量出来的年龄
        /// （距当前 <c>X-Timestamp</c> 签发那一刻），
        /// **不能用文件名里的 createUnixSec 减当前墙上时间** —— 玩家改系统时间会让年龄
        /// 凭空变成负数或几小时，导致该重签的不重签、不该重签的乱重签（全局硬约束 2）。
        /// </para>
        /// <para>
        /// <paramref name="hasSignature"/> 为 false 覆盖两种情形，都必须重签名：
        /// 批次第一次发送，以及**进程重启后**（单调锚点跨不过进程，一律按需要重签名处理
        /// —— 保守且正确：多算一次 HMAC，换掉"带着几小时前的时间戳去撞 ±300 秒窗口"）。
        /// </para>
        /// </summary>
        internal static LogUploadSignPlan PlanSignature(bool hasSignature, long ageMs, LogConfig cfg)
        {
            if (!hasSignature || cfg == null)
            {
                return LogUploadSignPlan.Resign;
            }

            if (ageMs < 0L)
            {
                // 单调时钟不该倒流。真倒流了（或锚点被写坏）就重签名，方向安全。
                return LogUploadSignPlan.Resign;
            }

            return ageMs >= cfg.ResignAfterSec * 1000L
                ? LogUploadSignPlan.Resign
                : LogUploadSignPlan.Reuse;
        }

        /// <summary>
        /// 压缩阈值（协议 §4.1）：请求体 <c>&lt; GzipMinBytes</c> 不压缩，<c>≥</c> 就 gzip。
        /// <para>
        /// ⚠️ 无论压不压，**签名始终基于压缩前字节**（payload_raw）—— 那是协议里最容易
        /// 搞错的一点，也是 <c>code=2</c> 的高发原因（协议 §7）。
        /// </para>
        /// </summary>
        internal static bool ShouldGzip(int payloadBytes, LogConfig cfg)
        {
            return cfg != null && payloadBytes >= cfg.GzipMinBytes;
        }
    }

    /// <summary>
    /// 一个批次在本次会话里的上传状态。**全部是内存态** ——
    /// 进程重启就清零，这正是"进程重启 == 一次网络恢复"成立的原因（协议 §4.4）。
    /// </summary>
    internal sealed class LogUploadBatchState
    {
        /// <summary>outbox 文件名（不含目录）。</summary>
        internal readonly string Name;

        /// <summary>
        /// <c>hex(sha256(payload_raw))</c> 的缓存。
        /// <para>
        /// 它是**payload 派生**的，与时间无关，因此时钟 offset 变化时**不需要作废**
        /// （只作废时间戳与签名）。协议 §4.4 的实现建议正是"首次发送时缓存 sha256，
        /// 窗口内重试零计算，补传时只做一次 HMAC"。
        /// </para>
        /// </summary>
        internal string ShaHex;

        /// <summary>
        /// 当前这一份 <c>X-Timestamp</c>（秒，字符串形态）。
        /// ⚠️ 它与参与签名的 timestamp 是**同一个变量**（协议 §3.3 要点 3 / §7）。
        /// </summary>
        internal string TimestampText;

        /// <summary>与 <see cref="TimestampText"/> 配套的 <c>X-Signature</c>。</summary>
        internal string Signature;

        /// <summary>
        /// <see cref="TimestampText"/> 签发那一刻的**单调**时刻（<see cref="LogSystem.UptimeMs"/>）。
        /// 年龄 = 当前单调时刻 − 它。
        /// </summary>
        internal long AnchorUptimeMs;

        /// <summary>有一份可复用的时间戳+签名。false = 必须重签名。</summary>
        internal bool HasSignature;

        /// <summary>**本次连通性事件内**已失败的次数。不是终生配额。</summary>
        internal int Attempts;

        /// <summary>
        /// 预算耗尽 → 暂停。⚠️ **暂停不是删除**：文件留在 outbox 里，
        /// 等 <see cref="ILogUploader.OnNetworkResumed"/> 重置预算，
        /// 最终淘汰只由 TTL 与容量决定（票 08 负责）。
        /// </summary>
        internal bool Paused;

        /// <summary>
        /// 最近一次失败属于哪一类。**只有 <see cref="LogUploadOutcome.NetworkUnavailable"/>
        /// 会被网络恢复重置**。
        /// </summary>
        internal LogUploadOutcome LastFailure;

        /// <summary>下一次可发送的单调时刻（退避到期）。</summary>
        internal long NextDueUptimeMs;

        internal LogUploadBatchState(string name)
        {
            Name = name;
            LastFailure = LogUploadOutcome.Success;
        }

        /// <summary>
        /// 作废缓存的请求头与签名，**保留 sha256 摘要**（它与时间无关）。
        /// 时钟 offset 变化时调用 —— 不作废的话会带着校正前的旧签名一路重试到预算耗尽
        /// （旧时间戳必然继续落在窗口外，必然继续失败）。
        /// </summary>
        internal void InvalidateSignature()
        {
            TimestampText = null;
            Signature = null;
            HasSignature = false;
            AnchorUptimeMs = 0L;
        }
    }

    /// <summary>一次失败之后该怎么办。</summary>
    internal struct LogUploadRetryDecision
    {
        /// <summary>
        /// true = 预算耗尽，批次**暂停**（留在 outbox，不删除）。
        /// </summary>
        internal bool Paused;

        /// <summary>退避毫秒数。<see cref="Paused"/> 为 true 时无意义。</summary>
        internal int DelayMs;

        /// <summary>本次连通性事件内累计失败次数，诊断用。</summary>
        internal int Attempts;
    }

    /// <summary>
    /// 批次上传状态的账本：重试预算、退避到期、**删除失败黑名单**。
    ///
    /// <para>
    /// 它是纯内存 + 注入时钟的，因此整套"预算耗尽是暂停不是删除""只有网络类失败被恢复重置"
    /// "黑名单里的文件本次会话不再被返回"都能在 EditMode 里直接摆出来断言。
    /// </para>
    /// <para>
    /// ⚠️ 线程安全：<see cref="OnNetworkResumed"/> 与 <see cref="InvalidateSignatures"/>
    /// 由**主线程**调用（网络状态变化、地址下发），其余由**上传线程**调用，故整体加锁。
    /// 锁只护内存字典，不护任何 IO。
    /// </para>
    /// </summary>
    internal sealed class LogUploadRetryBook
    {
        private readonly LogConfig m_Config;
        private readonly object m_Lock = new object();

        private readonly Dictionary<string, LogUploadBatchState> m_States =
            new Dictionary<string, LogUploadBatchState>(StringComparer.Ordinal);

        /// <summary>
        /// ⚠️ **删除失败黑名单**（票 09「重试」末条）：发送成功但文件删不掉时记在这里，
        /// **本次会话不再发送它，下次启动重试删除**。
        /// <para>
        /// 没有这一条，一个删不掉的文件会导致**无限重发、无限烧流量** ——
        /// 下一轮扫描又会把它排在 FIFO 队首，永远发不完。
        /// </para>
        /// <para>
        /// 同一个集合也收"读不出来的文件"：一个 IO 一直失败的文件同样会卡住队首，
        /// 处理方式（本次会话跳过、下次启动重来）完全一致。
        /// </para>
        /// </summary>
        private readonly HashSet<string> m_Skip = new HashSet<string>(StringComparer.Ordinal);

        internal LogUploadRetryBook(LogConfig cfg)
        {
            m_Config = cfg ?? new LogConfig();
        }

        /// <summary>黑名单里的文件数，诊断与测试用。</summary>
        internal int SkippedCount
        {
            get { lock (m_Lock) { return m_Skip.Count; } }
        }

        /// <summary>该文件本次会话是否已被跳过（删不掉 / 读不出来）。</summary>
        internal bool IsSkipped(string name)
        {
            lock (m_Lock)
            {
                return name != null && m_Skip.Contains(name);
            }
        }

        /// <summary>
        /// 取**FIFO 队首第一个现在就能发的批次**（串行发送，一次一批）。
        ///
        /// <para>
        /// <paramref name="pendingFifo"/> 直接来自 <see cref="LogOutbox.PendingNames"/>，
        /// 它已经是字典序 == 时间序 == FIFO，**本方法不重新排序、也不自己扫目录**
        /// （票 08 的索引是容量与顺序的唯一事实源）。
        /// </para>
        /// <para>跳过三类：黑名单、已暂停、退避未到期。</para>
        /// <para>
        /// 顺带**剪掉已经不在 outbox 里的状态与黑名单项**（被容量淘汰或 TTL 删掉的），
        /// 否则一次长会话的字典会随淘汰单调增长。
        /// </para>
        /// </summary>
        /// <param name="dueInMs">
        /// 队列里最近一个"只是还没到期"的批次还要等多久；没有这种批次时是 0。
        /// 调用方据此决定睡多久，避免空转。
        /// </param>
        internal string NextSendable(IReadOnlyList<string> pendingFifo, long nowMs, out int dueInMs)
        {
            dueInMs = 0;
            if (pendingFifo == null || pendingFifo.Count == 0)
            {
                lock (m_Lock)
                {
                    m_States.Clear();
                    m_Skip.Clear();
                }

                return null;
            }

            lock (m_Lock)
            {
                Prune(pendingFifo);

                long soonest = long.MaxValue;
                string chosen = null;

                for (int i = 0; i < pendingFifo.Count; i++)
                {
                    string name = pendingFifo[i];
                    if (name == null || m_Skip.Contains(name))
                    {
                        continue;
                    }

                    LogUploadBatchState state;
                    if (!m_States.TryGetValue(name, out state))
                    {
                        // 从没试过 —— 立刻可发。
                        chosen = name;
                        break;
                    }

                    if (state.Paused)
                    {
                        continue;
                    }

                    long remain = state.NextDueUptimeMs - nowMs;
                    if (remain <= 0L)
                    {
                        chosen = name;
                        break;
                    }

                    if (remain < soonest)
                    {
                        soonest = remain;
                    }
                }

                if (chosen == null && soonest != long.MaxValue)
                {
                    dueInMs = soonest > int.MaxValue ? int.MaxValue : (int)soonest;
                }

                return chosen;
            }
        }

        /// <summary>取（或建）某个批次的状态。</summary>
        internal LogUploadBatchState State(string name)
        {
            lock (m_Lock)
            {
                LogUploadBatchState state;
                if (!m_States.TryGetValue(name, out state))
                {
                    state = new LogUploadBatchState(name);
                    m_States[name] = state;
                }

                return state;
            }
        }

        /// <summary>
        /// 记一次失败，并给出"退避多久"或"暂停"。
        /// <para>
        /// ⚠️ 预算耗尽返回 <see cref="LogUploadRetryDecision.Paused"/> = true，
        /// 调用方**不得据此删除文件**。
        /// </para>
        /// </summary>
        internal LogUploadRetryDecision Fail(string name, LogUploadOutcome outcome, long nowMs, double jitter)
        {
            lock (m_Lock)
            {
                LogUploadBatchState state;
                if (!m_States.TryGetValue(name, out state))
                {
                    state = new LogUploadBatchState(name);
                    m_States[name] = state;
                }

                state.Attempts++;
                state.LastFailure = outcome;

                int delay;
                if (LogUploadPolicy.TryBackoffDelayMs(state.Attempts, m_Config, jitter, out delay))
                {
                    state.NextDueUptimeMs = nowMs + delay;
                    return new LogUploadRetryDecision
                    {
                        Paused = false,
                        DelayMs = delay,
                        Attempts = state.Attempts,
                    };
                }

                state.Paused = true;
                return new LogUploadRetryDecision
                {
                    Paused = true,
                    DelayMs = 0,
                    Attempts = state.Attempts,
                };
            }
        }

        /// <summary>发送成功：这个批次的状态不再需要了（文件即将被删）。</summary>
        internal void Succeed(string name)
        {
            lock (m_Lock)
            {
                m_States.Remove(name);
            }
        }

        /// <summary>
        /// 本次会话跳过这个文件（删不掉 / 读不出来）。见 <see cref="m_Skip"/>。
        /// </summary>
        internal void Skip(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            lock (m_Lock)
            {
                m_Skip.Add(name);
                m_States.Remove(name);
            }
        }

        /// <summary>
        /// 网络恢复：**重置"网络不可用"那一类的预算**（协议 §4.4 / specs/0004）。
        ///
        /// <para>
        /// ⚠️ **服务端拒绝（拿到响应但 code != 0）不在此列** —— 它和网络无关，
        /// 一个 topic 配错的批次不该在每次网络抖动后重新烧一轮预算，
        /// 它只应该靠 TTL 淘汰。
        /// </para>
        /// <para>
        /// 触发时机（specs/0004）：网络从不可用变可用、从「无地址」变「有地址」、
        /// 进程重启（内存态天然清零，不需要显式调用）。
        /// </para>
        /// <para>
        /// ⚠️ 这里**不作废签名** —— 签名的有效期由重签名窗口判定，
        /// 与网络状态无关（作废签名的唯一理由是时钟 offset 变化）。
        /// </para>
        /// </summary>
        /// <returns>被重置的批次数，诊断用。</returns>
        internal int OnNetworkResumed()
        {
            lock (m_Lock)
            {
                int reset = 0;
                foreach (KeyValuePair<string, LogUploadBatchState> entry in m_States)
                {
                    LogUploadBatchState state = entry.Value;
                    if (state.LastFailure != LogUploadOutcome.NetworkUnavailable)
                    {
                        continue;
                    }

                    state.Attempts = 0;
                    state.Paused = false;
                    state.NextDueUptimeMs = 0L;
                    reset++;
                }

                return reset;
            }
        }

        /// <summary>
        /// ⚠️ **时钟 offset 变化：立刻作废所有缓存的请求头与签名**（票 09 / specs/0004）。
        /// 不做的话会带着校正前的旧时间戳一路重试下去，必然继续失败直到预算耗尽。
        /// <para>sha256 摘要保留 —— 它是 payload 派生的，与时间无关。</para>
        /// </summary>
        /// <returns>被作废的批次数，诊断用。</returns>
        internal int InvalidateSignatures()
        {
            lock (m_Lock)
            {
                int count = 0;
                foreach (KeyValuePair<string, LogUploadBatchState> entry in m_States)
                {
                    if (entry.Value.HasSignature)
                    {
                        count++;
                    }

                    entry.Value.InvalidateSignature();
                }

                return count;
            }
        }

        /// <summary>调用方已持锁。</summary>
        private void Prune(IReadOnlyList<string> pendingFifo)
        {
            if (m_States.Count == 0 && m_Skip.Count == 0)
            {
                return;
            }

            var alive = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < pendingFifo.Count; i++)
            {
                if (pendingFifo[i] != null)
                {
                    alive.Add(pendingFifo[i]);
                }
            }

            RemoveMissing(m_States, alive);

            if (m_Skip.Count > 0)
            {
                // 文件已经不在 outbox 里（被 TTL / 容量淘汰）就不必再记着跳过它。
                m_Skip.RemoveWhere(name => !alive.Contains(name));
            }
        }

        private static void RemoveMissing(
            Dictionary<string, LogUploadBatchState> states, HashSet<string> alive)
        {
            List<string> doomed = null;
            foreach (KeyValuePair<string, LogUploadBatchState> entry in states)
            {
                if (!alive.Contains(entry.Key))
                {
                    doomed = doomed ?? new List<string>();
                    doomed.Add(entry.Key);
                }
            }

            if (doomed == null)
            {
                return;
            }

            for (int i = 0; i < doomed.Count; i++)
            {
                states.Remove(doomed[i]);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════════════
    //  与票 10 的接缝（**票 10 已落地**）
    //
    //  地址选择与时钟校正的真实实现在 LogAddressBook.cs / LogClock.cs。
    //  这里定义两者的接口 + 票 09 期间的最小实现；票 10 只换了 Install() 里装配的实现，
    //  **上传循环的选址与取时间戳那几行一字未改** —— 这正是当初把它们抽成接口的目的。
    //
    //  ⚠️ 票 10 在接口上**加了两个成员**（不是改语义）：
    //    · ILogUploadAddresses.CurrentTimeUrl —— 校时必须问"正在拒绝我们的那台服务器"
    //    · ILogUploadClock.OnTimestampRejected —— code=5 的重锚机会
    //  两个接口都是 S4 自己在票 09 建的内部接缝，**不是契约 C4 的成员**，故不涉及契约变更。
    // ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>X-Timestamp</c> 的时钟来源。
    ///
    /// <para>
    /// ⚠️ **必须是独立时钟，不能建立在 <c>TimeHelper</c> 之上**（ADR-0002 末段 / specs/0004）：
    /// 登录时 TimeHelper 会被服务器时间同步而跳变，offset 叠在一个已经正确的时钟上会
    /// 把请求反向推出窗口，而"只校正首次 code=5"的策略让它再也无法恢复。
    /// </para>
    /// <para>
    /// ⚠️ 这个时钟**只服务 <c>X-Timestamp</c>**。<c>logs[].ts</c> 永远是采集回调里
    /// <c>DateTime.UtcNow</c> 的原始值，**绝不使用本接口**（全局硬约束 5 / ADR-0002）——
    /// 补传的是几小时前发生的日志，改了 ts 事件时间线就错了。
    /// </para>
    /// <para>
    /// 10 号票用 <see cref="LogClock"/>（锚点 + 单调经过秒数 + <c>GET /v1/time</c> 重锚）
    /// 替换了默认实现。<see cref="Epoch"/> 是**给上传循环的通知机制**：
    /// 它一变，循环立刻作废所有缓存的请求头与签名。
    /// </para>
    /// <para>⚠️ 由上传线程读取，实现方不得调用任何 Unity API（全局硬约束 1）。</para>
    /// </summary>
    internal interface ILogUploadClock
    {
        /// <summary>当前 Unix **秒**（协议 §3.2：不是毫秒）。</summary>
        long NowUnixSec();

        /// <summary>
        /// 时钟基准的版本号。**每次 offset / 锚点变化就递增。**
        /// 上传循环每轮比对它，一变就作废所有缓存签名。
        /// </summary>
        int Epoch { get; }

        /// <summary>
        /// 收到 <c>code=5</c>（时间戳超出 ±300 秒窗口）时的**重锚机会**（票 10）。
        ///
        /// <para>
        /// ⚠️ 它**不是**给 <c>code=5</c> 写的特殊重试分支 —— 重试照旧走普通退避
        /// （协议 §3.6 明确要求不给它写特例）。这里只是顺手把时钟校准：
        /// <c>code=5</c> 本身就是"我的时间不对"这个诊断信号最直接的来源。
        /// </para>
        /// <para>
        /// ⚠️ **不是只做一次**：每次 <c>code=5</c> 都可以重锚，限流由实现方负责
        /// （见 <see cref="LogClock.MinResyncIntervalMs"/>）。"只校正首次"正是早期方案
        /// 无法恢复的直接原因。
        /// </para>
        /// </summary>
        /// <param name="timeUrl">
        /// 当前地址上的 <c>/v1/time</c>（<see cref="ILogUploadAddresses.CurrentTimeUrl"/>）。
        /// </param>
        /// <param name="nowMs">单调时刻（<see cref="LogSystem.UptimeMs"/>），限流与锚点都用它。</param>
        /// <returns>
        /// <see cref="LogClockResync.Anchored"/> 时调用方必须立刻作废所有缓存的签名与请求头。
        /// </returns>
        LogClockResync OnTimestampRejected(string timeUrl, long nowMs, CancellationToken token);
    }

    /// <summary>
    /// 设备墙上时钟，**没有任何校正**。
    /// <para>
    /// 10 号票之前的生产行为，现在退居为**构造兜底**（<c>clock == null</c> 时用它）
    /// 与"未校正"这一档的语义参照物。协议 §4.3 明确允许"什么都不做"，
    /// 代价是时钟偏差超 ±300 秒的设备会持续拿到 <c>code=5</c> —— 而那正是
    /// <see cref="LogClock"/> 要解决的事，生产路径一律用它。
    /// </para>
    /// <para>
    /// <see cref="Epoch"/> 恒为 0、<see cref="OnTimestampRejected"/> 恒为
    /// <see cref="LogClockResync.Disabled"/> —— 没有校正，就没有需要作废的东西。
    /// </para>
    /// <para><c>DateTime.UtcNow</c> 是 BCL，后台线程安全（全局硬约束 1）。</para>
    /// </summary>
    internal sealed class LogUploadDeviceClock : ILogUploadClock
    {
        internal static readonly LogUploadDeviceClock Instance = new LogUploadDeviceClock();

        private static readonly DateTime UnixEpochUtc =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private LogUploadDeviceClock()
        {
        }

        public long NowUnixSec()
        {
            return (long)(DateTime.UtcNow - UnixEpochUtc).TotalSeconds;
        }

        public int Epoch
        {
            get { return 0; }
        }

        public LogClockResync OnTimestampRejected(string timeUrl, long nowMs, CancellationToken token)
        {
            // 本实现不校时：没有锚点可以重设，也就没有缓存签名需要作废。
            return LogClockResync.Disabled;
        }
    }

    /// <summary>
    /// 上报地址的来源（协议 §4.2）。
    ///
    /// <para>
    /// ⚠️ **会话内选址状态机在 <see cref="LogAddressBook"/>**（票 10）：优先用第一个 →
    /// 失败换下一个 → 成功后粘住本次会话 → 全部失败回到第一个重新开始一轮。
    /// 上传循环只需要"有没有地址"和"现在用哪个"，所以这几件事被抽成了接口，
    /// 票 10 换实现时循环一行未改。
    /// </para>
    /// <para>⚠️ <see cref="Set"/> 由主线程调用，其余由上传线程调用，实现方必须自己保证线程安全。</para>
    /// </summary>
    internal interface ILogUploadAddresses
    {
        /// <summary>
        /// 收下地址列表。**null / 空数组 = 停发**，但不影响打包
        /// （日志继续进 outbox，等地址恢复后补传，靠 <c>OutboxMaxBytes</c> 自然约束）。
        /// </summary>
        void Set(string[] addrs);

        /// <summary>
        /// 有可用地址。false 时上传线程**休眠**，⚠️ **不做退避重试** ——
        /// 没有东西可以重试，空转只会烧电（specs/0004「没有地址 vs 有地址但失败」）。
        /// </summary>
        bool HasAddress { get; }

        /// <summary>当前该用的完整 URL；没有地址时为 null。</summary>
        string Current { get; }

        /// <summary>
        /// 当前地址上的免签名时间诊断接口 <c>GET /v1/time</c>（协议 §3.7）；没有地址时为 null。
        /// <para>
        /// ⚠️ 与 <see cref="Current"/> **必须是同一个地址**：校时要问"正在拒绝我们的那台服务器"，
        /// 问另一台等于拿 A 的时间去对 B 的窗口 —— 那会把一次本来能自愈的偏差校成两个偏差。
        /// </para>
        /// </summary>
        string CurrentTimeUrl { get; }

        /// <summary>发送成功 → "成功后粘住本次会话"。</summary>
        void ReportSuccess();

        /// <summary>连不上 → "失败换下一个"。</summary>
        void ReportFailure();
    }

    /// <summary>
    /// 最小地址实现：**永远用列表里第一个非空地址**。
    ///
    /// <para>
    /// ⚠️ **已被 <see cref="LogAddressBook"/> 取代（票 10），不在任何生产路径上** ——
    /// <see cref="LogUploader.Install"/> 装配的是 <see cref="LogAddressBook"/>。
    /// 保留它只为两件事：<c>addresses == null</c> 时的构造兜底，以及作为"停发语义"和
    /// "URL 组装"这两组既有 EditMode 用例的被测对象。**新代码一律用 <see cref="LogAddressBook"/>。**
    /// </para>
    /// <para>
    /// 它的局限写在类名里：第一个地址挂掉时不会换下一个，批次会一路退避到暂停。
    /// 这个代价只存在于 09→10 之间，现已消除。
    /// </para>
    /// <para>
    /// 线程安全靠 volatile 换一个**不可变副本**：主线程构造新数组后整体换引用，
    /// 上传线程只读 —— 与契约 C2 的快照做法同形。
    /// 复制一份也避免了调用方之后改动那个数组（<c>EnableUpload</c> 的实参来自网络层）。
    /// </para>
    /// </summary>
    internal sealed class LogUploadFirstAddress : ILogUploadAddresses
    {
        private volatile string m_Current;
        private volatile string m_CurrentTimeUrl;

        public void Set(string[] addrs)
        {
            string first = FirstUsable(addrs);
            m_CurrentTimeUrl = first == null ? null : LogAddressBook.BuildTimeUrl(first);
            m_Current = first == null ? null : LogAddressBook.BuildBatchUrl(first);
        }

        public bool HasAddress
        {
            get { return m_Current != null; }
        }

        public string Current
        {
            get { return m_Current; }
        }

        public string CurrentTimeUrl
        {
            get { return m_CurrentTimeUrl; }
        }

        public void ReportSuccess()
        {
            // 只有一个地址，无所谓"粘住"。真实实现见 LogAddressBook.ReportSuccess。
        }

        public void ReportFailure()
        {
            // 换下一个是 LogAddressBook 的事。本实现刻意什么都不做，见类注释。
        }

        /// <summary>
        /// 把 dispatch 下发的一条地址拼成完整 URL。
        /// <para>
        /// ⚠️ 组装规则**只有一份**，在 <see cref="LogAddressBook.BuildBatchUrl"/> 里
        /// （票 10 把它连同 <c>/v1/time</c> 的组装一起收拢了）。这里转发过去，
        /// 免得两份实现随时间漂移成两种 URL。
        /// </para>
        /// </summary>
        internal static string BuildUrl(string addr)
        {
            return LogAddressBook.BuildBatchUrl(addr);
        }

        /// <summary>返回**原始地址**（不是 URL）：调用方还要拿它拼 <c>/v1/time</c>。</summary>
        private static string FirstUsable(string[] addrs)
        {
            if (addrs == null)
            {
                return null;
            }

            for (int i = 0; i < addrs.Length; i++)
            {
                if (LogAddressBook.BuildBatchUrl(addrs[i]) != null)
                {
                    return addrs[i];
                }
            }

            return null;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════════════
    //  传输层
    // ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>一次 POST 的全部输入。**纯数据**，方便在不碰网络的前提下断言组装结果。</summary>
    internal struct LogUploadRequest
    {
        internal string Url;

        /// <summary>请求体：gzip 之后的字节，或 payload_raw 本身（看阈值）。</summary>
        internal byte[] Body;

        /// <summary>body 是 gzip 的 → 必须带 <c>Content-Encoding: gzip</c>（协议 §3.1）。</summary>
        internal bool Gzipped;

        internal string AppId;

        /// <summary>⚠️ 与参与签名的 timestamp 是**同一个字符串**（协议 §3.3 要点 3）。</summary>
        internal string TimestampText;

        internal string Signature;
    }

    /// <summary>
    /// 传输层的原始结果。**刻意不含任何"成功与否"的判断** ——
    /// 判定全在 <see cref="LogUploadPolicy.Classify"/> 里，而它只看 body。
    /// </summary>
    internal struct LogUploadTransportResult
    {
        /// <summary>
        /// 拿到了一个 HTTP 响应（**无论状态码是多少**）。
        /// false = 连接层错误 / TLS 失败 / 超时 / 被 Stop 取消。
        /// </summary>
        internal bool GotResponse;

        /// <summary>
        /// HTTP 状态码。
        /// ⚠️ **只进诊断日志，不参与成功判定**（协议 §3.6：所有响应一律 200）。
        /// </summary>
        internal int StatusCode;

        /// <summary>响应体原文。判定的唯一输入。</summary>
        internal string Body;

        /// <summary>没拿到响应时的原因，诊断用。</summary>
        internal string Error;

        internal static LogUploadTransportResult Responded(int statusCode, string body)
        {
            return new LogUploadTransportResult
            {
                GotResponse = true,
                StatusCode = statusCode,
                Body = body ?? "",
            };
        }

        internal static LogUploadTransportResult NoResponse(string error)
        {
            return new LogUploadTransportResult
            {
                GotResponse = false,
                StatusCode = 0,
                Body = "",
                Error = error ?? "unknown",
            };
        }
    }

    /// <summary>
    /// 发送一个请求。抽成接口只为一件事：让上传循环的决策部分不依赖真实网络。
    /// <para>⚠️ 实现方跑在上传线程上，不得调用任何 Unity API（全局硬约束 1）。</para>
    /// </summary>
    internal interface ILogUploadTransport
    {
        /// <summary>
        /// **不得抛异常**：一切失败都翻译成
        /// <see cref="LogUploadTransportResult.NoResponse"/>。
        /// 抛出去会掀翻上传线程，而那表现成"CLS 里再也没有日志"。
        /// </summary>
        LogUploadTransportResult Send(LogUploadRequest request, CancellationToken token);
    }

#if !UNITY_WEBGL
    /// <summary>
    /// <c>System.Net.Http.HttpClient</c> 传输实现。
    ///
    /// <para>
    /// **选型已由真机探针定案**（票 02 的 Android IL2CPP release 包）：
    /// IL2CPP 下 HttpClient 能在后台线程走通 HTTPS，HMACSHA256 / SHA256 也没被裁剪。
    /// 因此不需要"后台线程攒批 + 主线程 UnityWebRequest"的退回方案
    /// （<c>UnityWebRequest</c> 只能在主线程用，与"上传在后台线程"直接冲突）。
    /// </para>
    /// <para>
    /// ⚠️ **单例 HttpClient**：每次 new 一个会让每个请求各开一条连接，
    /// TIME_WAIT 累积到 socket 耗尽（这是 .NET 里最经典的一个坑）。
    /// 单例同时满足协议 §3.1 的"开启 keep-alive 复用连接"。
    /// </para>
    /// <para>
    /// ⚠️ 超时有**两道**：<c>HttpClient.Timeout</c>（= <see cref="LogConfig.HttpTimeoutMs"/>）
    /// 与调用方传进来的 <see cref="CancellationToken"/>。后者是
    /// <see cref="ILogUploader.Stop"/> 能"立刻返回"的关键 —— 只靠前者的话，
    /// 玩家每次退出游戏都要等在飞的请求自己超时（10 秒）。
    /// </para>
    /// </summary>
    internal sealed class LogHttpUploadTransport : ILogUploadTransport
    {
        private const string HeaderAppId = "X-App-Id";
        private const string HeaderTimestamp = "X-Timestamp";
        private const string HeaderSignature = "X-Signature";
        private const string ContentTypeJson = "application/json";
        private const string ContentEncodingGzip = "gzip";

        private static readonly object s_ClientLock = new object();
        private static HttpClient s_Client;

        private readonly int m_TimeoutMs;

        internal LogHttpUploadTransport(int timeoutMs)
        {
            m_TimeoutMs = timeoutMs > 0 ? timeoutMs : new LogConfig().HttpTimeoutMs;
        }

        public LogUploadTransportResult Send(LogUploadRequest request, CancellationToken token)
        {
            if (string.IsNullOrEmpty(request.Url) || request.Body == null)
            {
                return LogUploadTransportResult.NoResponse("no address");
            }

            try
            {
                using (var message = new HttpRequestMessage(HttpMethod.Post, request.Url))
                using (var content = new ByteArrayContent(request.Body))
                {
                    // Content-Type 始终是 application/json（协议 §3.2）——
                    // 压缩只改 body 的编码，不改它的媒体类型。
                    content.Headers.ContentType = new MediaTypeHeaderValue(ContentTypeJson);
                    if (request.Gzipped)
                    {
                        content.Headers.ContentEncoding.Add(ContentEncodingGzip);
                    }

                    message.Content = content;

                    // TryAddWithoutValidation：这几个都是自定义头，不需要 BCL 的语法校验，
                    // 而校验失败会抛异常 —— 在发送路径上多一个异常源没有任何收益。
                    message.Headers.TryAddWithoutValidation(HeaderAppId, request.AppId ?? "");
                    message.Headers.TryAddWithoutValidation(HeaderTimestamp, request.TimestampText ?? "");
                    message.Headers.TryAddWithoutValidation(HeaderSignature, request.Signature ?? "");

                    // 同步等待：本方法跑在我们自己的后台线程上，没有 Unity 的同步上下文，
                    // 不存在死锁风险，也不需要把整条循环改成 async。
                    using (HttpResponseMessage response = Shared(m_TimeoutMs)
                        .SendAsync(message, HttpCompletionOption.ResponseContentRead, token)
                        .GetAwaiter().GetResult())
                    {
                        string body = response.Content != null
                            ? response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                            : "";

                        // ⚠️ 这里**不看也不判**状态码，只把它带回去给诊断日志。
                        // 不调 EnsureSuccessStatusCode：协议一律回 200，
                        // 而万一网关改写了状态码，body 里的 code 仍然是权威结论。
                        return LogUploadTransportResult.Responded((int)response.StatusCode, body);
                    }
                }
            }
            catch (Exception e)
            {
                // 连接失败、DNS、TLS、超时、Stop 取消 —— 一律"没拿到响应"。
                // ⚠️ 绝不能把它当成成功：那会删掉本地文件。
                return LogUploadTransportResult.NoResponse(Describe(e));
            }
        }

        /// <summary>
        /// **进程内唯一的 <see cref="HttpClient"/>**（懒建）。
        /// <para>
        /// ⚠️ 票 10 把它从私有实例方法提升成 internal static：
        /// <see cref="LogHttpServerTimeSource"/>（<c>GET /v1/time</c>）必须复用同一个客户端。
        /// 为校时单开一个等于把"每次 new 一个 HttpClient → TIME_WAIT 累积 → socket 耗尽"
        /// 那个经典坑在旁边再踩一遍，而且白丢一条已经建好的 keep-alive 连接（协议 §3.1）。
        /// </para>
        /// <para>
        /// <c>Timeout</c> 只在首次创建时生效（<see cref="HttpClient"/> 发出第一个请求后
        /// 不允许再改），所以上报与校时共享同一个 <see cref="LogConfig.HttpTimeoutMs"/> ——
        /// 两者本来就是同一个参数。
        /// </para>
        /// </summary>
        internal static HttpClient Shared(int timeoutMs)
        {
            lock (s_ClientLock)
            {
                if (s_Client == null)
                {
                    s_Client = new HttpClient();
                    s_Client.Timeout = TimeSpan.FromMilliseconds(
                        timeoutMs > 0 ? timeoutMs : new LogConfig().HttpTimeoutMs);
                }

                return s_Client;
            }
        }

        /// <summary>
        /// 异常的简短描述。<c>.GetAwaiter().GetResult()</c> 会解包
        /// <see cref="AggregateException"/>，但嵌套的场景仍然存在，所以再剥一层内层异常
        /// —— "TaskCanceledException" 这种外壳对排查毫无帮助。
        /// </summary>
        private static string Describe(Exception e)
        {
            Exception inner = e;
            while (inner.InnerException != null)
            {
                inner = inner.InnerException;
            }

            return inner.GetType().Name + ": " + inner.Message;
        }
    }
#endif

    // ════════════════════════════════════════════════════════════════════════════════════
    //  诊断日志（全局硬约束 6 / 票 09「诊断可观测性」）
    // ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 上传通道的内部事件类别，用于**限频**：同一类事件在
    /// <see cref="LogConfig.SuppressNoticeSec"/> 窗口内只放行一条。
    ///
    /// <para>
    /// ⚠️ **为什么限频是这里的生死线**（票 09 点名的一条）：上传失败 → 打一条诊断 →
    /// 这条诊断被采集 → 进下一个批次 → 进 outbox → 上传失败 → 再打一条……
    /// 不限频的话 CLS 里最后全是系统的自我报告，真正的业务日志被自己挤掉。
    /// 挡住它需要**两道**，缺一不可：
    /// </para>
    /// <list type="number">
    ///   <item><b>重入守卫</b>（<see cref="LogSystem.InternalError"/> 里的 <c>[ThreadStatic]</c>
    ///   标志）挡的是**同一个调用栈内**的递归：打诊断 → 采集回调同步回来 → 再打诊断。</item>
    ///   <item><b>限频</b>（本枚举 + <see cref="LogUploadNoticeGate"/>）挡的是**时间维度**上的
    ///   自我放大：每次失败都报一条，一个 60 秒的断网就能刷出几十条。</item>
    /// </list>
    /// <para>
    /// 另外本类型的调用点一律**按状态跃迁**报告（第一次失败、预算耗尽、恢复），
    /// 并把被压住的次数累加进下一条 —— 这是第三道，也是最有效的一道。
    /// </para>
    /// <para>
    /// ⚠️ 沿用票 03/05 已有的这套机制（<c>SuppressNoticeSec</c> 窗口 + 单调时钟 +
    /// <c>InternalError</c> 的重入守卫），**不另起一套**。之所以在这里重新实现窗口判定，
    /// 是因为 <c>LogSystem.AllowInternalEvent</c> 与 <c>LogInternalEvent</c> 都是 S1 的私有成员，
    /// 而 <c>LogSystem.cs</c> 不属于本票（票 08 的 <c>LogOutbox.ReportEviction</c>
    /// 出于同样的原因做了同样的事）。
    /// </para>
    /// </summary>
    internal enum LogUploadNotice
    {
        /// <summary>发送成功（聚合计数报告）。</summary>
        Uploaded = 0,

        /// <summary>服务端拒绝：拿到响应但 code != 0，或 body 畸形。</summary>
        Rejected,

        /// <summary>网络不可用：没拿到 HTTP 响应。</summary>
        Unreachable,

        /// <summary>重试预算耗尽 → 批次暂停（**不是删除**）。</summary>
        Exhausted,

        /// <summary>发送成功但文件删不掉 → 进内存黑名单。</summary>
        DeleteFailed,

        /// <summary>批次文件读不出来 → 本次会话跳过。</summary>
        Unreadable,

        /// <summary>地址列表变化 / 当前地址切换。</summary>
        AddressChanged,

        /// <summary>时钟 offset 变化 → 已作废全部缓存签名。</summary>
        SignaturesInvalidated,

        /// <summary>
        /// <c>code=5</c> 触发的一次 <c>GET /v1/time</c> 重锚（成功或失败）。
        /// <para>
        /// ⚠️ 真机验收场景 C 点名要"logcat 里看得见客户端自动调了一次 <c>/v1/time</c>"，
        /// 这一行就是那个证据。
        /// </para>
        /// </summary>
        ClockResynced,

        /// <summary>网络恢复 → 已重置预算。</summary>
        Resumed,

        /// <summary>枚举成员计数，用作限频数组长度。**永远放在最后。**</summary>
        Count,
    }

    /// <summary>
    /// 内部事件的限频闸。窗口**复用 <see cref="LogConfig.SuppressNoticeSec"/>**
    /// （票 05 的裁定：参数总表是唯一事实源，不为限频新增字段），
    /// 间隔用**单调时钟**（全局硬约束 2）。
    /// <para>
    /// ⚠️ 第一次总是放行 —— 单调时钟的 0 时刻是合法值，拿"上次时刻 == 0"当"没发生过"
    /// 会让进程刚起来的头 60 秒里第一条事件被莫名其妙地吃掉（与 S1 同一个坑）。
    /// </para>
    /// </summary>
    internal sealed class LogUploadNoticeGate
    {
        private readonly LogConfig m_Config;
        private readonly object m_Lock = new object();
        private readonly long[] m_LastMs = new long[(int)LogUploadNotice.Count];
        private readonly bool[] m_Seen = new bool[(int)LogUploadNotice.Count];

        internal LogUploadNoticeGate(LogConfig cfg)
        {
            m_Config = cfg ?? new LogConfig();
        }

        /// <returns>true = 这一条可以发出去，并且窗口从 <paramref name="nowMs"/> 重新计时。</returns>
        internal bool Allow(LogUploadNotice kind, long nowMs)
        {
            int i = (int)kind;
            if (i < 0 || i >= m_Seen.Length)
            {
                return true;
            }

            long window = m_Config.SuppressNoticeSec * 1000L;

            lock (m_Lock)
            {
                if (m_Seen[i] && nowMs - m_LastMs[i] < window)
                {
                    return false;
                }

                m_Seen[i] = true;
                m_LastMs[i] = nowMs;
                return true;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════════════
    //  上传循环
    // ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 契约 C4 的 <see cref="ILogUploader"/> 实现（票 09 / specs 0004）：
    /// 单个后台线程，**串行**地把 outbox 里的批次一个个发出去，
    /// **只有 body 里 <c>code == 0</c> 才删本地文件**。
    ///
    /// <para>
    /// 与打包线程之间**唯一的耦合就是 outbox 目录**：一个只写、一个只读删，
    /// 两边故障完全隔离（specs/0004）。扫描与删除一律走
    /// <see cref="LogOutbox.PendingNames"/> / <see cref="LogOutbox.Delete"/> ——
    /// 绕过它直接 <c>File.Delete</c> 会让票 08 的容量索引虚高，上限从此失准。
    /// </para>
    ///
    /// <para>
    /// ⚠️ **上传线程不得调用任何 Unity API**（全局硬约束 1）。
    /// 唯一例外是诊断日志走的 <c>Debug.Log*</c>（全局硬约束 6 明文要求，
    /// 真机联调靠它在 logcat 里筛）。
    /// 顺带注意 <c>FileUtils.GetMD5Hash</c> 用的是静态共享的 MD5 实例、**不是线程安全的**
    /// （契约 C4 补记），本线程一律不碰它；签名走 <see cref="LogSigner"/>，
    /// 它内部每次新建 hash 实例正是为此。
    /// </para>
    ///
    /// <para>
    /// ⚠️ **所有时间间隔用单调时钟** <see cref="LogSystem.UptimeMs"/>（全局硬约束 2）：
    /// 退避到期、重签名年龄、诊断限频。墙上时钟只用于 <c>X-Timestamp</c>
    /// （那是请求新鲜度锚点，本来就必须是墙上时间）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ **只在 Android / iOS 启用**（全局硬约束 3 的平台矩阵）。
    /// 其余平台 <see cref="Start"/> 直接返回，**连线程都不起** ——
    /// Editor / Standalone 上 outbox 照常堆积，只是没有消费者。
    /// </para>
    /// </summary>
    internal sealed class LogUploader : ILogUploader
    {
        /// <summary>
        /// 平台开关。用 <c>#if</c> 而不是 <c>Application.platform</c>：
        /// 本类跑在后台线程上，碰任何 Unity API 都违反全局硬约束 1（与票 08 同一个做法）。
        /// <para>
        /// 写成 <c>static readonly</c> 而不是 <c>const</c>：<c>const false</c> 会让
        /// <c>if (!PlatformSupported) return;</c> 之后的整段被判成不可达代码（CS0162）。
        /// </para>
        /// </summary>
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
        internal static readonly bool PlatformSupported = true;
#else
        internal static readonly bool PlatformSupported = false;
#endif

        /// <summary>
        /// 协议 §3.6 的「时间戳超出 ±300 秒窗口」结果码。
        ///
        /// <para>
        /// ⚠️ 它**不是**一个可调参数（所以是 const 而不是 <see cref="LogConfig"/> 字段），
        /// 也**不给重试逻辑开特例**：<c>code=5</c> 和其它非 0 码一样走普通退避
        /// （协议 §3.6 / specs/0004 末段）。它在代码里只出现在一个地方 ——
        /// 判断"要不要顺手校一次时钟"，见 <see cref="TryResyncClock"/>。
        /// </para>
        /// </summary>
        private const long TimestampOutOfWindowCode = 5L;

        private readonly LogConfig m_Config;
        private readonly LogUploadRetryBook m_Book;
        private readonly LogUploadNoticeGate m_Notices;
        private readonly ILogUploadAddresses m_Addresses;
        private readonly ILogUploadClock m_Clock;
        private readonly ILogUploadTransport m_Transport;

        /// <summary>退避抖动的来源。**只在上传线程上使用**（Random 不是线程安全的）。</summary>
        private readonly Random m_Jitter = new Random();

        private readonly object m_StartLock = new object();

        /// <summary>
        /// 取消在飞的请求。<see cref="Stop"/> 靠它做到"立刻返回" ——
        /// 见 <see cref="Stop"/> 的注释。
        /// </summary>
        private readonly CancellationTokenSource m_Cancel = new CancellationTokenSource();

        /// <summary>
        /// 上传线程的唤醒信号。三个来源：<see cref="Stop"/>、<see cref="SetAddresses"/>、
        /// <see cref="OnNetworkResumed"/>。没有它，一次"地址刚下发"要多等一个轮询间隔。
        /// </summary>
        private readonly AutoResetEvent m_Wake = new AutoResetEvent(false);

        private Thread m_Thread;

        /// <summary>跨线程读写，必须 volatile。</summary>
        private volatile bool m_Running;

        /// <summary>已经 <see cref="Stop"/> 过。本实例不再可用（见 <see cref="Stop"/>）。</summary>
        private volatile bool m_Stopped;

        /// <summary>上一轮观察到的时钟版本。它一变就作废所有缓存签名。</summary>
        private int m_ClockEpoch;

        /// <summary>上一次真正用过的地址，用来识别"地址切换"这个诊断事件。</summary>
        private string m_LastUrl;

        // ── 诊断聚合：被限频压住的那些**累加**进下一条，报小了比不报更坏（票 05 的裁定）。──
        private int m_PendingUploaded;
        private int m_PendingRejected;
        private int m_PendingUnreachable;

        /// <summary>
        /// 真实实现的装配与**接线**：从 <see cref="LogSystem"/> 取配置，装上票 10 的
        /// <see cref="LogAddressBook"/> 与 <see cref="LogClock"/>、HttpClient 传输层，
        /// 然后把自己挂到 <see cref="LogSystem.Uploader"/> 上并起线程。
        ///
        /// <para>
        /// 之所以把"赋值 + Start"也放进来：契约 C4 补记只把 <c>LogSystem.cs</c> 里
        /// **一行**接线授权给票 10（那是 S1 的文件），所以这一行必须自带全部装配动作。
        /// </para>
        /// <para>
        /// ⚠️ **幂等**：宿主重复调 <see cref="LogSystem.Init"/> 时先停掉上一个实例。
        /// 不停的话两条上传线程会并发扫同一个 outbox 发同一批 —— 协议 §4.1 要求串行，
        /// 而且重复发送会双倍烧玩家的上行流量。<c>Stop</c> 立刻返回，不阻塞启动。
        /// </para>
        /// <para>
        /// 非 Android / iOS 上 <see cref="Start"/> 直接返回、连线程都不起（平台矩阵），
        /// 因此 Editor 与 Standalone 上这次装配只是换了一个不工作的消费者，没有副作用。
        /// </para>
        /// </summary>
        internal static LogUploader Install()
        {
            LogConfig cfg = LogSystem.Config;

            var previous = LogSystem.Uploader as LogUploader;
            if (previous != null)
            {
                previous.Stop();
            }

            var uploader = new LogUploader(
                cfg,
                new LogAddressBook(),
                new LogClock(cfg, CreateTimeSource(cfg)),
                CreateTransport(cfg));

            LogSystem.Uploader = uploader;
            uploader.Start();
            return uploader;
        }

        internal LogUploader(
            LogConfig cfg,
            ILogUploadAddresses addresses,
            ILogUploadClock clock,
            ILogUploadTransport transport)
        {
            m_Config = cfg ?? new LogConfig();
            m_Addresses = addresses ?? new LogUploadFirstAddress();
            m_Clock = clock ?? LogUploadDeviceClock.Instance;
            m_Transport = transport;
            m_Book = new LogUploadRetryBook(m_Config);
            m_Notices = new LogUploadNoticeGate(m_Config);
            m_ClockEpoch = m_Clock.Epoch;
        }

        /// <summary>账本。诊断与测试用。</summary>
        internal LogUploadRetryBook Book
        {
            get { return m_Book; }
        }

        /// <summary>
        /// 起上传线程。**幂等**；非 Android / iOS 上直接返回，连线程都不起。
        /// </summary>
        public void Start()
        {
            if (!PlatformSupported || m_Stopped || m_Transport == null)
            {
                return;
            }

            lock (m_StartLock)
            {
                if (m_Thread != null)
                {
                    return;
                }

                m_Running = true;
                m_Thread = new Thread(Loop);
                m_Thread.Name = "UmbreeyLogUpload";

                // 后台线程：它绝不能拖住进程退出。Stop 不 Join，理由见 Stop。
                m_Thread.IsBackground = true;
                m_Thread.Start();
            }
        }

        /// <summary>
        /// 停上传线程。⚠️ **立刻返回，并取消在飞的请求**
        /// （契约 C4 补记 / 票 06 停止顺序的第四步）。
        ///
        /// <para>
        /// 三步都是非阻塞的：
        /// </para>
        /// <list type="number">
        ///   <item>置 <see cref="m_Running"/> = false —— 循环跑完当前这一轮就退出。</item>
        ///   <item><b><see cref="CancellationTokenSource.Cancel"/></b> —— 在飞的
        ///   <c>SendAsync</c> **当场**抛 <c>OperationCanceledException</c>，
        ///   被传输层翻译成"没拿到响应"。这是关键的一步：
        ///   <see cref="LogConfig.HttpTimeoutMs"/> 是 10 秒，实现成"等它自己超时"的话
        ///   **玩家每次退出游戏都要卡 10 秒**。</item>
        ///   <item>置位 <see cref="m_Wake"/> —— 线程若正睡在退避等待上，立刻醒来看见
        ///   <see cref="m_Running"/> 已经是 false 并退出。</item>
        /// </list>
        /// <para>
        /// ⚠️ **不 Join 线程**：Join 就等于把"立刻返回"变成"等它退出"。
        /// 线程是 <c>IsBackground</c>，进程退出时不会被它拖住；
        /// 而它当前这一轮最坏也只是把一次已被取消的请求收个尾。
        /// </para>
        /// <para>
        /// ⚠️ **也不 Dispose <see cref="m_Wake"/> / <see cref="m_Cancel"/>**：
        /// 上传线程可能正在 <c>WaitOne</c> / <c>SendAsync</c> 上用着它们，
        /// 释放会让它撞 <see cref="ObjectDisposedException"/>。
        /// 两个句柄随进程回收，代价是零。
        /// </para>
        /// <para>
        /// 由此本实例**一次性**：Stop 之后不再可用，重新 <c>Init</c> 会换一个新实例
        /// （<see cref="Install"/>）。这比"能重启的线程"简单得多，也不会出现
        /// "旧线程还没退、新线程已经在发"的两份并发发送。
        /// </para>
        /// </summary>
        public void Stop()
        {
            m_Stopped = true;
            m_Running = false;

            try
            {
                m_Cancel.Cancel();
            }
            catch (Exception)
            {
                // 已经取消过 / 已经释放：无所谓，目的已经达到。
            }

            try
            {
                m_Wake.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// 收下地址列表（契约 C4）。
        /// <para>
        /// ⚠️ **null / 空数组 = 停发，但不影响打包** —— 日志继续进 outbox 等地址恢复后补传。
        /// 服务端故障远比"永久关闭"常见（specs/0004）。
        /// </para>
        /// <para>
        /// 「无地址 → 有地址」是 specs/0004 规定的 <see cref="OnNetworkResumed"/> 时机之一：
        /// 之前因为没有地址而堆积的批次应当立刻重新参与发送。
        /// </para>
        /// </summary>
        public void SetAddresses(string[] addrs)
        {
            bool had = m_Addresses.HasAddress;
            m_Addresses.Set(addrs);
            bool has = m_Addresses.HasAddress;

            Notice(
                LogUploadNotice.AddressChanged,
                false,
                "upload addresses updated: " + (addrs == null ? 0 : addrs.Length)
                + " entry(ies), uploading is " + (has ? "enabled" : "paused (batches keep piling up in the outbox)"));

            if (!had && has)
            {
                OnNetworkResumed();
                return;
            }

            Wake();
        }

        /// <summary>
        /// 网络恢复：**重置所有因"网络不可用"而暂停的批次的预算**，重新参与发送。
        /// <para>
        /// ⚠️ 服务端拒绝那一类**不重置**（它和网络无关）。见
        /// <see cref="LogUploadRetryBook.OnNetworkResumed"/>。
        /// </para>
        /// </summary>
        public void OnNetworkResumed()
        {
            int reset = m_Book.OnNetworkResumed();
            if (reset > 0)
            {
                Notice(
                    LogUploadNotice.Resumed,
                    false,
                    "network resumed: retry budget reset for " + reset + " paused batch(es)");
            }

            Wake();
        }

        private static ILogUploadTransport CreateTransport(LogConfig cfg)
        {
#if !UNITY_WEBGL
            return new LogHttpUploadTransport((cfg ?? new LogConfig()).HttpTimeoutMs);
#else
            // WebGL / 小游戏：平台矩阵里这一行是"只留接口和空实现"（全局硬约束 3）。
            return null;
#endif
        }

        /// <summary>
        /// <c>GET /v1/time</c> 的执行者。null 时 <see cref="LogClock"/> 退化成"有锚点才校正、
        /// 永远拿不到锚点"，即设备墙上时钟 —— 与 WebGL 上连传输层都没有是一致的。
        /// </summary>
        private static ILogServerTimeSource CreateTimeSource(LogConfig cfg)
        {
#if !UNITY_WEBGL
            return new LogHttpServerTimeSource((cfg ?? new LogConfig()).HttpTimeoutMs);
#else
            return null;
#endif
        }

        private void Wake()
        {
            try
            {
                m_Wake.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// 上传线程主循环。**串行**：一轮最多发一个批次，绝不并发多发（协议 §4.1）。
        /// <para>
        /// 等待时长由 <see cref="Tick"/> 给出：有东西可发就是 0（立刻发下一批）、
        /// 退避中就是"距最近一个批次到期还剩多久"、没地址 / 没批次就睡一个轮询间隔。
        /// </para>
        /// </summary>
        private void Loop()
        {
            while (m_Running)
            {
                int waitMs;

                try
                {
                    waitMs = Tick();
                }
                catch (Exception e)
                {
                    // 上传线程绝不能因为一轮出错就没了 —— 那表现成"CLS 里再也没有日志"。
                    Notice(LogUploadNotice.Unreachable, false, "upload round failed: " + e.Message);
                    waitMs = PollIntervalMs;
                }

                if (!m_Running)
                {
                    break;
                }

                try
                {
                    m_Wake.WaitOne(waitMs);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// 轮询间隔：**复用 <see cref="LogConfig.BatchLingerMs"/>**。
        /// <para>
        /// 它的语义恰好就是"下一个批次最快多久会出现"，因此不需要为上传线程新增一个
        /// 参数（参数总表是唯一事实源）。地址下发、网络恢复、Stop 都会直接唤醒线程，
        /// 所以这个间隔只决定"新批次被发现的最坏延迟"。
        /// </para>
        /// </summary>
        private int PollIntervalMs
        {
            get { return m_Config.BatchLingerMs > 0 ? m_Config.BatchLingerMs : 1000; }
        }

        /// <summary>一轮：选一个批次发掉，返回下一轮该等多久。</summary>
        private int Tick()
        {
            // ① 时钟 offset 变了 → **立刻作废所有缓存的请求头与签名**。
            ApplyClockEpoch();

            // ② 没有地址 = **休眠**，不做退避重试（没有东西可以重试，空转只会烧电）。
            if (!m_Addresses.HasAddress)
            {
                return PollIntervalMs;
            }

            LogOutbox outbox = LogOutbox.Instance;
            if (outbox == null)
            {
                // 宿主还没调 Init，或 outbox 目录建不出来。没有数据源，睡。
                return PollIntervalMs;
            }

            // ③ FIFO 取第一个能发的。扫描与顺序都用票 08 的索引，不自己扫目录。
            long now = LogSystem.UptimeMs;
            int dueInMs;
            string name = m_Book.NextSendable(outbox.PendingNames(), now, out dueInMs);
            if (name == null)
            {
                return dueInMs > 0 && dueInMs < PollIntervalMs ? dueInMs : PollIntervalMs;
            }

            SendOne(outbox, name, now);

            // 串行地接着发下一批。失败的那个已经带上了退避到期时间，不会被立刻重选。
            return 0;
        }

        /// <summary>
        /// ⚠️ **时钟锚点一变，立刻作废所有缓存的请求头与签名**（票 09 / 票 10 / specs/0004）。
        ///
        /// <para>
        /// 不作废的后果是必然的失败循环：旧签名对应的是**校正前**的时间戳，
        /// 它必然继续落在服务端 ±300 秒窗口外，于是继续 <c>code=5</c>、继续退避、
        /// 一路烧到预算耗尽 —— 校时白做了。
        /// </para>
        /// <para>
        /// ⚠️ **sha256 摘要保留**（<see cref="LogUploadBatchState.ShaHex"/>）：
        /// 它是 payload 派生的，与时间无关，作废它只是白算一遍哈希。
        /// </para>
        /// <para>
        /// 两处调用：每轮开头（覆盖"别处改了锚点"的一般情形），以及
        /// <see cref="TryResyncClock"/> 里重锚成功之后立刻调一次 ——
        /// 后者让"锚点更新后立即作废"不依赖循环的调度顺序。
        /// </para>
        /// </summary>
        /// <returns>被作废的批次数，诊断与测试用。</returns>
        internal int ApplyClockEpoch()
        {
            int epoch = m_Clock.Epoch;
            if (epoch == m_ClockEpoch)
            {
                return 0;
            }

            m_ClockEpoch = epoch;
            int invalidated = m_Book.InvalidateSignatures();
            Notice(
                LogUploadNotice.SignaturesInvalidated,
                false,
                "log clock changed: dropped cached headers and signatures for "
                + invalidated + " batch(es); they will be re-signed on the next attempt");

            return invalidated;
        }

        /// <summary>
        /// 发一个批次：读 payload_raw → 判断是否需要重签名 → 算签名 → 按阈值决定是否 gzip →
        /// POST → **看 body 的 code**（specs/0004「上传循环」）。
        /// </summary>
        private void SendOne(LogOutbox outbox, string name, long nowMs)
        {
            byte[] payload = ReadPayload(outbox, name);
            if (payload == null)
            {
                return;
            }

            LogUploadBatchState state = m_Book.State(name);

            string url = m_Addresses.Current;
            if (url == null)
            {
                // 刚好在这一瞬间被 EnableUpload(null) 停掉了。下一轮会进"休眠"分支。
                return;
            }

            ReportAddressIfChanged(url);

            LogUploadRequest request = BuildRequest(state, payload, url, nowMs);

            LogUploadTransportResult transport = m_Transport.Send(request, m_Cancel.Token);

            // ⚠️ 整个通道的成功判定只有这一行，而它只看 body。
            LogUploadResponse response;
            LogUploadOutcome outcome = LogUploadPolicy.Classify(transport, out response);

            if (outcome == LogUploadOutcome.Success)
            {
                OnSuccess(outbox, name);
                return;
            }

            OnFailure(name, outcome, response, transport, nowMs);
        }

        /// <summary>
        /// 组装请求。三条硬要求都落在这里：
        /// <list type="number">
        ///   <item>年龄用**单调时钟**量（<see cref="LogSystem.UptimeMs"/> − 锚点），
        ///   不用文件名里的 createUnixSec 减墙上时间。</item>
        ///   <item>重签名时**payload 一个字节都不动**，只换 <c>X-Timestamp</c> 并拿缓存的
        ///   sha256 重算一次 HMAC。<c>logs[].ts</c> 因此永远保持原值
        ///   （全局硬约束 5 / ADR-0002 末段）。</item>
        ///   <item><c>X-Timestamp</c> 与参与签名的 timestamp 是**同一个变量**（协议 §7）。</item>
        /// </list>
        /// </summary>
        private LogUploadRequest BuildRequest(
            LogUploadBatchState state, byte[] payload, string url, long nowMs)
        {
            // payload 派生，与时间无关：一个批次一辈子只算一次（协议 §4.4 的实现建议）。
            if (state.ShaHex == null)
            {
                state.ShaHex = LogSigner.Sha256Hex(payload);
            }

            long ageMs = state.HasSignature ? nowMs - state.AnchorUptimeMs : 0L;
            LogUploadSignPlan plan = LogUploadPolicy.PlanSignature(state.HasSignature, ageMs, m_Config);

            if (plan == LogUploadSignPlan.Resign)
            {
                // 一个变量，两处使用：X-Timestamp 头 与 StringToSign 的第一行。
                string timestampText = m_Clock.NowUnixSec().ToString(CultureInfo.InvariantCulture);

                state.TimestampText = timestampText;
                state.Signature = LogSigner.Sign(LogSystem.AppSecret ?? "", timestampText, state.ShaHex);
                state.AnchorUptimeMs = nowMs;
                state.HasSignature = true;
            }

            // 阈值判断在调用方（LogSigner.Gzip 无条件压缩）。签名基于**压缩前**字节 ——
            // 上面那几行已经算完了，这里只换 body。
            bool gzip = LogUploadPolicy.ShouldGzip(payload.Length, m_Config);

            return new LogUploadRequest
            {
                Url = url,
                Body = gzip ? LogSigner.Gzip(payload) : payload,
                Gzipped = gzip,
                AppId = LogSystem.AppId ?? "",
                TimestampText = state.TimestampText,
                Signature = state.Signature,
            };
        }

        /// <summary>
        /// 读一个批次的 payload_raw。
        /// <para>
        /// ⚠️ 批次**可能超过 <see cref="LogConfig.BatchMaxBytes"/>**（契约 C4 补记：
        /// 单条 64KB 的 msg 全是控制字符时转义后可达 480KB）。
        /// 这里刻意**不做任何大小断言**，照发不误 —— 压缩后仍远低于协议 1MB 硬限，
        /// 而丢弃等于让最该看的那条日志消失。
        /// </para>
        /// <para>
        /// 读不出来分两种：文件已经不在了（被 TTL / 容量淘汰）→ 忘掉它；
        /// 文件在但读失败 → **本次会话跳过**，否则它会永远卡在 FIFO 队首，
        /// 后面的批次一条都发不出去。
        /// </para>
        /// </summary>
        private byte[] ReadPayload(LogOutbox outbox, string name)
        {
            string dir = outbox.DirPath;
            if (string.IsNullOrEmpty(dir))
            {
                return null;
            }

            string path = Path.Combine(dir, name);

            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length > 0)
                {
                    return bytes;
                }

                // 0 字节的 .json 不可能是一个合法批次（原子写保证要么完整要么不存在）。
                // 发出去只会换回 code=1，白烧一轮预算。
                m_Book.Skip(name);
                outbox.Delete(name);
                return null;
            }
            catch (FileNotFoundException)
            {
                ForgetMissing(outbox, name);
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                ForgetMissing(outbox, name);
                return null;
            }
            catch (Exception e)
            {
                m_Book.Skip(name);
                Notice(
                    LogUploadNotice.Unreadable,
                    true,
                    "outbox batch unreadable, skipped for this session (" + name + "): " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// 文件已经不在磁盘上了（被外部删掉、或目录被清空）。
        ///
        /// <para>
        /// ⚠️ **必须把它从票 08 的索引里摘掉**，否则 <see cref="LogOutbox.PendingNames"/>
        /// 会永远把这个名字排在队首：读 → FileNotFound → 再读 → …… 上传线程会以 0ms
        /// 间隔空转烧 CPU。<see cref="LogOutbox.Delete"/> 对不存在的文件返回 true
        /// （<c>File.Delete</c> 不会为此抛异常），正好用来同步索引。
        /// </para>
        /// </summary>
        private void ForgetMissing(LogOutbox outbox, string name)
        {
            m_Book.Succeed(name);

            if (!outbox.Delete(name))
            {
                // 索引摘不掉（目录都没了）。跳过它，免得下一轮又来一遍。
                m_Book.Skip(name);
            }
        }

        /// <summary>
        /// <c>code == 0</c>：**删本地文件**（协议 §3.6）。
        /// <para>
        /// ⚠️ 删除失败 → **内存黑名单**，本次会话不再发送它，下次启动重试删除。
        /// 没有这一条，一个删不掉的文件会一直排在 FIFO 队首被无限重发、无限烧流量。
        /// </para>
        /// <para>删除走 <see cref="LogOutbox.Delete"/> 以同步维护票 08 的容量索引。</para>
        /// </summary>
        private void OnSuccess(LogOutbox outbox, string name)
        {
            m_Book.Succeed(name);
            m_Addresses.ReportSuccess();

            if (!outbox.Delete(name))
            {
                m_Book.Skip(name);
                Notice(
                    LogUploadNotice.DeleteFailed,
                    true,
                    "uploaded but could not delete the local batch (" + name
                    + "); skipping it for this session, will retry the delete next launch");
            }

            m_PendingUploaded++;
            if (Allow(LogUploadNotice.Uploaded))
            {
                int total = m_PendingUploaded;
                m_PendingUploaded = 0;
                Emit(false, "uploaded " + total + " batch(es) since the last notice, code=0");
            }
        }

        /// <summary>
        /// 失败：退避重试，或预算耗尽后**暂停**。
        /// <para>
        /// ⚠️ 两类失败的诊断文案必须分开 —— "没连上"和"服务端不收"的排查方向完全不同，
        /// 而真机上这一行常常是唯一的线索（协议 §7）。
        /// </para>
        /// </summary>
        private void OnFailure(
            string name,
            LogUploadOutcome outcome,
            LogUploadResponse response,
            LogUploadTransportResult transport,
            long nowMs)
        {
            LogUploadRetryDecision decision = m_Book.Fail(name, outcome, nowMs, m_Jitter.NextDouble());

            // ⚠️ 换不换地址按**节点级 / 请求级**分流，不是按"有没有拿到 HTTP 响应"分 ——
            //    判定与全部理由在 LogUploadPolicy.ShouldSwitchAddress 里（含 code=4 那个反例）。
            //    这一行刻意排在两个诊断分支之前：两类失败都可能是节点级的。
            bool switchAddress = LogUploadPolicy.ShouldSwitchAddress(outcome, response);
            if (switchAddress)
            {
                m_Addresses.ReportFailure();
            }

            if (outcome == LogUploadOutcome.NetworkUnavailable)
            {
                m_PendingUnreachable++;

                if (Allow(LogUploadNotice.Unreachable))
                {
                    int total = m_PendingUnreachable;
                    m_PendingUnreachable = 0;
                    Emit(
                        true,
                        "upload unreachable (no HTTP response) " + total
                        + " time(s) since the last notice, last error: " + transport.Error
                        + "; retry " + decision.Attempts + "/" + m_Config.RetryMax
                        + (decision.Paused ? " (budget spent)" : " in " + decision.DelayMs + "ms"));
                }
            }
            else
            {
                m_PendingRejected++;

                if (Allow(LogUploadNotice.Rejected))
                {
                    int total = m_PendingRejected;
                    m_PendingRejected = 0;

                    // HTTP 状态码只在这一行出现，**纯诊断**，判定不看它（协议 §3.6）。
                    Emit(
                        true,
                        "upload rejected by the server " + total + " time(s) since the last notice, last "
                        + response.Describe() + " (http " + transport.StatusCode
                        + ", not used for the decision); retry " + decision.Attempts + "/" + m_Config.RetryMax
                        + (decision.Paused ? " (budget spent)" : " in " + decision.DelayMs + "ms")
                        + (switchAddress
                            ? "; node-level code -> switching to the next address"
                            : "; request-level code -> keeping the current address"));
                }
            }

            // ⚠️ code=5 顺手校一次时钟。**这不是给 code=5 写的重试特例** ——
            //    退避与预算在上面已经按普通失败算完了，这里只是利用"服务端说我时间不对"
            //    这个信号去重锚（票 10 / specs/0004「时钟」）。
            if (outcome == LogUploadOutcome.ServerRejected
                && response.HasCode
                && response.Code == TimestampOutOfWindowCode)
            {
                TryResyncClock(nowMs);
            }

            if (decision.Paused)
            {
                Notice(
                    LogUploadNotice.Exhausted,
                    true,
                    "retry budget spent for " + name + " after " + decision.Attempts
                    + " attempt(s) (" + outcome
                    + "); the batch is PAUSED and kept on disk, not deleted"
                    + (outcome == LogUploadOutcome.NetworkUnavailable
                        ? " — it resumes when the network comes back"
                        : " — a server rejection is not reset by a network resume, only TTL and capacity retire it"));
            }
        }

        /// <summary>
        /// <c>code=5</c> → 调免签名的 <c>GET /v1/time</c> 重设时钟锚点（票 10）。
        ///
        /// <para>
        /// ⚠️ **每次 <c>code=5</c> 都可以重锚，不是只做一次** —— "只校正首次"正是早期
        /// <c>TimeHelper + offset</c> 方案再也无法恢复的直接原因（ADR-0002 末段）。
        /// 刷请求的风险由 <see cref="LogClock.MinResyncIntervalMs"/> 的最小间隔挡住：
        /// 服务端持续返回 5 时，每个批次的每次重试都会走到这里，
        /// 但只有跨过最小间隔的那一次会真的发请求。
        /// </para>
        /// <para>
        /// 校时用**当前正在发送的那个地址**（<see cref="ILogUploadAddresses.CurrentTimeUrl"/>）：
        /// 拒绝我们的是它，它的时间才是那个 ±300 秒窗口的中心。
        /// </para>
        /// <para>
        /// ⚠️ 重锚成功后**立刻**作废所有缓存的请求头与签名，不等下一轮循环
        /// —— 见 <see cref="ApplyClockEpoch"/>。
        /// </para>
        /// </summary>
        private void TryResyncClock(long nowMs)
        {
            string timeUrl = m_Addresses.CurrentTimeUrl;
            if (string.IsNullOrEmpty(timeUrl))
            {
                return;
            }

            LogClockResync result;
            try
            {
                result = m_Clock.OnTimestampRejected(timeUrl, nowMs, m_Cancel.Token);
            }
            catch (Exception e)
            {
                // 校时是"顺手做"的事，它绝不能把这一轮上传掀翻。
                Notice(LogUploadNotice.ClockResynced, false, "clock resync failed: " + e.Message);
                return;
            }

            if (result == LogClockResync.Anchored)
            {
                // 先作废，再报告：诊断日志本身也要经过采集管线，别让它插在两者之间。
                int invalidated = ApplyClockEpoch();
                Notice(
                    LogUploadNotice.ClockResynced,
                    false,
                    "code=" + TimestampOutOfWindowCode + " -> GET " + LogAddressBook.TimePath
                    + " re-anchored the log clock (" + Describe(m_Clock) + "), "
                    + invalidated + " cached signature(s) dropped");
                return;
            }

            if (result == LogClockResync.Failed)
            {
                var clock = m_Clock as LogClock;
                Notice(
                    LogUploadNotice.ClockResynced,
                    true,
                    "code=" + TimestampOutOfWindowCode + " but GET " + LogAddressBook.TimePath
                    + " gave no usable server_time: "
                    + (clock != null ? clock.LastError : "unavailable")
                    + "; the timestamp stays as it is and the batch keeps retrying");
            }

            // Throttled / Disabled：刻意静默 —— 它们每秒都可能发生，报出来只会挤掉真正的线索。
        }

        private static string Describe(ILogUploadClock clock)
        {
            var real = clock as LogClock;
            return real != null ? real.Describe() : "device clock";
        }

        private void ReportAddressIfChanged(string url)
        {
            if (string.Equals(m_LastUrl, url, StringComparison.Ordinal))
            {
                return;
            }

            string previous = m_LastUrl;
            m_LastUrl = url;

            Notice(
                LogUploadNotice.AddressChanged,
                false,
                previous == null
                    ? "upload address in use: " + url
                    : "upload address switched: " + previous + " -> " + url);
        }

        private bool Allow(LogUploadNotice kind)
        {
            return m_Notices.Allow(kind, LogSystem.UptimeMs);
        }

        /// <summary>限频 + 发一条诊断（全局硬约束 6）。</summary>
        private void Notice(LogUploadNotice kind, bool error, string message)
        {
            if (!Allow(kind))
            {
                return;
            }

            Emit(error, message);
        }

        /// <summary>
        /// 走 <c>Debug.Log*</c> 保证 logcat 可见（全局硬约束 6）。
        /// <para>
        /// 这些行会被采集回调标成 <see cref="LogRecord.Internal"/>：
        /// **bypass 限流**、带**重入守卫**（<see cref="LogSystem.InternalError"/> 内的
        /// <c>[ThreadStatic]</c> 标志），并正常进本地卷和 outbox —— 联调需要在 CLS 里看到它们。
        /// </para>
        /// <para>
        /// ⚠️ 文案里**永远不出现 appSecret**（全局硬约束 4、8：它是"哪儿都不写"），
        /// 也不出现响应 body 原文（可能是被劫持的整页 HTML）。
        /// </para>
        /// </summary>
        private static void Emit(bool error, string message)
        {
            if (error)
            {
                LogSystem.InternalError("upload: " + message);
                return;
            }

            LogSystem.InternalInfo("upload: " + message);
        }
    }
}
