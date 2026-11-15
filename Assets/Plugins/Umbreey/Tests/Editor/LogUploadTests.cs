using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Umbreey.Tests
{
    /// <summary>
    /// 上传循环与重试的 EditMode 测试（票 09 / specs 0004 / 协议 §3 §4 §5）。
    ///
    /// <para>
    /// **网络本身不测**（价值低、脆弱），测的是**决策** —— 它们全都被写成了不碰网络、
    /// 不碰文件、不读时钟的纯函数（当前时刻与随机数由调用方注入）。
    /// 真实 HTTP / TLS / 服务端靠协议 §8 的联调步骤覆盖。
    /// </para>
    ///
    /// <para>
    /// 这里钉的是四件"错了就是日志静默消失"的事：
    /// <list type="number">
    ///   <item><b>成功判定只看 body 的 code</b> —— 连 HTTP 500 + <c>code=0</c> 都必须判成功。
    ///   按状态码判断会把每次失败都当成功、然后删掉本地文件。</item>
    ///   <item><b>畸形 body 一律判失败</b> —— 六种边界各一条用例。最阴的是"code 缺失"：
    ///   反序列化器把缺失的 int 默认成 0，而 0 正好是成功码。</item>
    ///   <item><b>预算耗尽是暂停不是删除</b>，且只有"网络不可用"那一类会被网络恢复重置。</item>
    ///   <item><b>重签名判定用年龄</b>（单调时钟量出来的），249s 与 251s 两侧行为不同。</item>
    /// </list>
    /// </para>
    /// </summary>
    public class LogUploadTests
    {
        private const string BatchA = "0000001000-aaaaaaaa-00001.json";
        private const string BatchB = "0000002000-aaaaaaaa-00002.json";

        private static LogConfig Config()
        {
            // 一律用参数总表的默认值（1s / 60s / 5 次 / 250s / 1KB），
            // 个别用例只改它真正要验的那一项。
            return new LogConfig();
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  退避序列（协议 §4.4）
        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 钉"**从 BackoffBeginMs 起**"：第一次重试的抖动带下沿正好是 1s，上沿是 1.25s。
        /// 退避从 0 起会变成立刻重连，那是惊群本身。
        /// </summary>
        [Test]
        public void Backoff_FirstAttempt_StartsAtBackoffBeginMs()
        {
            LogConfig cfg = Config();

            int low;
            int high;
            Assert.IsTrue(LogUploadPolicy.TryBackoffDelayMs(1, cfg, 0d, out low));
            Assert.IsTrue(LogUploadPolicy.TryBackoffDelayMs(1, cfg, 1d, out high));

            Assert.AreEqual(cfg.BackoffBeginMs, low);
            Assert.AreEqual(1250, high);
        }

        /// <summary>
        /// 钉"**指数增长**"：抖动量固定为 0 时，序列必须是 1/2/4/8/16 秒。
        /// 五次合计约 31 秒 —— 这正是"断网超过半分钟必须靠暂停兜住、不能耗尽即删"的由来。
        /// </summary>
        [Test]
        public void Backoff_GrowsExponentially()
        {
            LogConfig cfg = Config();
            int[] expected = { 1000, 2000, 4000, 8000, 16000 };

            int sum = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                int delay;
                Assert.IsTrue(LogUploadPolicy.TryBackoffDelayMs(i + 1, cfg, 0d, out delay));
                Assert.AreEqual(expected[i], delay, "attempt " + (i + 1));
                sum += delay;
            }

            Assert.AreEqual(31000, sum);
        }

        /// <summary>
        /// 钉"**封顶 BackoffMaxMs**"，同时钉"**封顶处抖动带不塌**"。
        /// <para>
        /// 后半条同样重要：如果饱和后所有客户端都精确地等 60 秒，惊群就在封顶处原样重现。
        /// 所以饱和时的带是 [48s, 60s]，而不是 [60s, 60s]。
        /// </para>
        /// </summary>
        [Test]
        public void Backoff_CapsAtBackoffMaxMs_AndKeepsJitterAtTheCap()
        {
            LogConfig cfg = Config();
            cfg.RetryMax = 12;   // 默认 5 次根本到不了封顶，要测封顶必须放开预算

            for (int attempt = 1; attempt <= cfg.RetryMax; attempt++)
            {
                int delay;
                Assert.IsTrue(LogUploadPolicy.TryBackoffDelayMs(attempt, cfg, 1d, out delay));
                Assert.LessOrEqual(delay, cfg.BackoffMaxMs, "attempt " + attempt + " exceeded the cap");
            }

            int low;
            int high;
            Assert.IsTrue(LogUploadPolicy.TryBackoffDelayMs(9, cfg, 0d, out low));
            Assert.IsTrue(LogUploadPolicy.TryBackoffDelayMs(9, cfg, 1d, out high));

            Assert.AreEqual(cfg.BackoffMaxMs, high);
            Assert.AreEqual(48000, low);
        }

        /// <summary>
        /// 钉"**含 jitter 且不同调用不完全相同**"（协议 §4.4：没有 jitter 会形成惊群）。
        /// 同时钉抖动仍然被限制在这一档的带宽内 —— 抖动不能把退避抖成 0 或抖到下一档。
        /// </summary>
        [Test]
        public void Backoff_JitterMakesDelaysDiffer()
        {
            LogConfig cfg = Config();
            var random = new Random(20260911);
            var seen = new HashSet<int>();

            for (int i = 0; i < 200; i++)
            {
                int delay;
                Assert.IsTrue(LogUploadPolicy.TryBackoffDelayMs(3, cfg, random.NextDouble(), out delay));

                // 第三次重试的基准是 4s，带宽 25%。
                Assert.GreaterOrEqual(delay, 4000);
                Assert.LessOrEqual(delay, 5000);
                seen.Add(delay);
            }

            Assert.Greater(seen.Count, 1, "backoff has no jitter: every call returned the same delay");
        }

        /// <summary>
        /// 钉"**RetryMax 次之后返回放弃**"。放弃的语义由调用方落实成"暂停"，见
        /// <see cref="Book_BudgetExhausted_PausesAndKeepsTheFile"/>。
        /// </summary>
        [Test]
        public void Backoff_GivesUpAfterRetryMax()
        {
            LogConfig cfg = Config();

            int delay;
            Assert.IsTrue(LogUploadPolicy.TryBackoffDelayMs(cfg.RetryMax, cfg, 0d, out delay));
            Assert.IsFalse(LogUploadPolicy.TryBackoffDelayMs(cfg.RetryMax + 1, cfg, 0d, out delay));
            Assert.AreEqual(0, delay);
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  重签名判定（协议 §4.4）
        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 钉阈值**下侧**：249 秒 → 原样重发，X-Timestamp 与签名都不变（窗口内重试零计算）。
        /// </summary>
        [Test]
        public void Resign_Age249Sec_ReusesSignature()
        {
            Assert.AreEqual(
                LogUploadSignPlan.Reuse,
                LogUploadPolicy.PlanSignature(true, 249L * 1000L, Config()));
        }

        /// <summary>
        /// 钉阈值**上侧**：251 秒 → 重新签名（payload 一个字节都不动，只换时间戳重算 HMAC）。
        /// 不重签的话服务端的 ±300 秒时间窗会把补传的批次全部拒成 code=5。
        /// </summary>
        [Test]
        public void Resign_Age251Sec_Resigns()
        {
            Assert.AreEqual(
                LogUploadSignPlan.Resign,
                LogUploadPolicy.PlanSignature(true, 251L * 1000L, Config()));
        }

        /// <summary>
        /// 钉边界取值本身：年龄 == ResignAfterSec 时算"该重签"（>=，留 50 秒余量的那一侧）。
        /// </summary>
        [Test]
        public void Resign_AgeExactlyAtThreshold_Resigns()
        {
            LogConfig cfg = Config();
            Assert.AreEqual(
                LogUploadSignPlan.Resign,
                LogUploadPolicy.PlanSignature(true, cfg.ResignAfterSec * 1000L, cfg));
        }

        /// <summary>
        /// 钉"**没有单调锚点一律按需要重签名处理**"：批次首次发送、以及**进程重启后**
        /// （内存态清零，锚点跨不过进程）都属于这一类。保守且正确 ——
        /// 代价是多算一次 HMAC，换掉"带着几小时前的时间戳去撞 ±300 秒窗口"。
        /// </summary>
        [Test]
        public void Resign_WithoutMonotonicAnchor_AlwaysResigns()
        {
            Assert.AreEqual(
                LogUploadSignPlan.Resign,
                LogUploadPolicy.PlanSignature(false, 0L, Config()));

            // 年龄为负（锚点被写坏 / 时钟异常）时同样倒向重签名，不是"当作很新"。
            Assert.AreEqual(
                LogUploadSignPlan.Resign,
                LogUploadPolicy.PlanSignature(true, -5000L, Config()));
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  响应解析（协议 §3.6）—— 本票最容易做错的一段
        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>钉 <c>code == 0</c> 是成功，且成功是**唯一**允许删本地文件的结论。</summary>
        [Test]
        public void Parse_CodeZero_IsSuccess()
        {
            LogUploadResponse response = LogUploadResponse.Parse(
                "{\"code\":0,\"message\":\"\",\"server_time\":1756881000}");

            Assert.IsTrue(response.HasCode);
            Assert.AreEqual(0L, response.Code);
            Assert.IsTrue(response.IsSuccess);
        }

        /// <summary>
        /// 钉"**不对 code 做分支处理**"：1/2/3/4/5/9 全部判失败、一视同仁。
        /// <para>
        /// ⚠️ <c>code=5</c>（时间戳超窗）也在其中 —— 它单独存在的意义纯粹是联调时能一眼
        /// 区分"时钟不对"和"签名写错"，**不是**让实现给它写特例。
        /// </para>
        /// </summary>
        [Test]
        public void Parse_AllNonZeroCodes_AreFailures()
        {
            long[] codes = { 1L, 2L, 3L, 4L, 5L, 9L };

            for (int i = 0; i < codes.Length; i++)
            {
                LogUploadResponse response = LogUploadResponse.Parse(
                    "{\"code\":" + codes[i] + ",\"message\":\"nope\"}");

                Assert.IsTrue(response.HasCode, "code " + codes[i]);
                Assert.AreEqual(codes[i], response.Code);
                Assert.IsFalse(response.IsSuccess, "code " + codes[i] + " must not be treated as success");
            }
        }

        /// <summary>
        /// ⚠️ **本票最关键的一条**：HTTP 状态码不参与判定。
        /// <para>
        /// 协议规定所有响应一律 HTTP 200，所以"状态码不是 200 就算失败"这种写法会在
        /// 网关改写状态码时把**已经被服务端收下**的批次判成失败、无限重发；
        /// 反过来"状态码 200 就算成功"会把每一次失败都当成功然后删掉本地文件。
        /// 这里构造 HTTP 500 + <c>code=0</c>，断言判为**成功**。
        /// </para>
        /// </summary>
        [Test]
        public void Classify_Http500WithCodeZero_IsSuccess()
        {
            LogUploadTransportResult transport =
                LogUploadTransportResult.Responded(500, "{\"code\":0,\"message\":\"\"}");

            LogUploadResponse response;
            LogUploadOutcome outcome = LogUploadPolicy.Classify(transport, out response);

            Assert.AreEqual(LogUploadOutcome.Success, outcome);
            Assert.AreEqual(500, transport.StatusCode, "the status code is kept for diagnostics only");
        }

        /// <summary>对称的一条：HTTP 200 + <c>code=3</c> 是失败，不能被 200 骗过去。</summary>
        [Test]
        public void Classify_Http200WithNonZeroCode_IsServerRejected()
        {
            LogUploadResponse response;
            LogUploadOutcome outcome = LogUploadPolicy.Classify(
                LogUploadTransportResult.Responded(200, "{\"code\":3}"), out response);

            Assert.AreEqual(LogUploadOutcome.ServerRejected, outcome);
            Assert.AreEqual(3L, response.Code);
        }

        /// <summary>畸形边界 1/6：<c>{}</c> —— 合法 JSON 对象，但没有 code。</summary>
        [Test]
        public void Parse_EmptyJsonObject_IsFailure()
        {
            AssertMalformed(LogUploadResponse.Parse("{}"));
        }

        /// <summary>
        /// 畸形边界 2/6：**code 字段缺失**。
        /// <para>
        /// ⚠️ 这是六种里最阴的一种：反序列化到带 <c>int code</c> 的类上会得到 0，
        /// 而 0 正好是成功码 —— 于是批次被判成功、本地文件被删掉、日志静默消失。
        /// </para>
        /// </summary>
        [Test]
        public void Parse_MissingCodeField_IsFailure()
        {
            AssertMalformed(LogUploadResponse.Parse("{\"message\":\"\",\"server_time\":1756881000}"));
        }

        /// <summary>畸形边界 3/6：code 是**字符串** <c>"0"</c>。宽容转换会把它变成成功。</summary>
        [Test]
        public void Parse_CodeAsString_IsFailure()
        {
            AssertMalformed(LogUploadResponse.Parse("{\"code\":\"0\"}"));
        }

        /// <summary>畸形边界 4/6：code 是 <c>null</c>。</summary>
        [Test]
        public void Parse_CodeNull_IsFailure()
        {
            AssertMalformed(LogUploadResponse.Parse("{\"code\":null}"));
        }

        /// <summary>畸形边界 5/6：body 为空（空串 / null / 纯空白）。</summary>
        [Test]
        public void Parse_EmptyBody_IsFailure()
        {
            AssertMalformed(LogUploadResponse.Parse(""));
            AssertMalformed(LogUploadResponse.Parse(null));
            AssertMalformed(LogUploadResponse.Parse("   "));
        }

        /// <summary>
        /// 畸形边界 6/6：body 不是 JSON（被网关换成 HTML、被截断）。
        /// **不抛异常** —— 判定层抛异常会掀翻上传线程，表现成"CLS 里再也没有日志"。
        /// </summary>
        [Test]
        public void Parse_NotJson_IsFailure()
        {
            AssertMalformed(LogUploadResponse.Parse("<html>502 Bad Gateway</html>"));
            AssertMalformed(LogUploadResponse.Parse("{\"code\":0"));   // 截断

            // 合法 JSON 但顶层不是对象，同样不能认。
            AssertMalformed(LogUploadResponse.Parse("0"));
            AssertMalformed(LogUploadResponse.Parse("[{\"code\":0}]"));
        }

        /// <summary>
        /// 钉两类失败的分界：**没拿到 HTTP 响应 = 网络不可用**。
        /// 它是唯一会被 <c>OnNetworkResumed</c> 重置预算的那一类。
        /// </summary>
        [Test]
        public void Classify_NoResponse_IsNetworkUnavailable()
        {
            LogUploadResponse response;
            LogUploadOutcome outcome = LogUploadPolicy.Classify(
                LogUploadTransportResult.NoResponse("SocketException: timed out"), out response);

            Assert.AreEqual(LogUploadOutcome.NetworkUnavailable, outcome);
            Assert.IsFalse(response.IsSuccess);
        }

        /// <summary>
        /// 钉"拿到响应但 body 畸形"算**服务端拒绝**而不是网络不可用 ——
        /// 它不该被网络恢复重置（网络明明是通的，是内容对不上）。
        /// </summary>
        [Test]
        public void Classify_MalformedBody_IsServerRejectedNotNetwork()
        {
            LogUploadResponse response;
            LogUploadOutcome outcome = LogUploadPolicy.Classify(
                LogUploadTransportResult.Responded(200, "{}"), out response);

            Assert.AreEqual(LogUploadOutcome.ServerRejected, outcome);
            Assert.IsFalse(response.HasCode);
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  压缩阈值（协议 §4.1）
        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 钉阈值两侧：1023 字节不压缩、1024 字节压缩。
        /// 签名始终基于压缩前字节，那一条由票 02 的签名测试与本票的组装代码共同保证。
        /// </summary>
        [Test]
        public void Gzip_ThresholdIsGzipMinBytes()
        {
            LogConfig cfg = Config();
            Assert.AreEqual(1024, cfg.GzipMinBytes);

            Assert.IsFalse(LogUploadPolicy.ShouldGzip(cfg.GzipMinBytes - 1, cfg));
            Assert.IsTrue(LogUploadPolicy.ShouldGzip(cfg.GzipMinBytes, cfg));
            Assert.IsTrue(LogUploadPolicy.ShouldGzip(cfg.GzipMinBytes + 1, cfg));
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  重试预算与账本
        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 钉"**预算耗尽是暂停，不是删除**"（协议 §4.4 / 票 09）。
        /// <para>
        /// 前 5 次失败都给出退避延迟，第 6 次才暂停；暂停之后该批次不再被选中发送，
        /// 但**文件仍然在 outbox 里**（账本里根本没有删除这个动作 ——
        /// 删除只由 <c>OnSuccess</c> 与票 08 的 TTL / 容量发起）。
        /// </para>
        /// </summary>
        [Test]
        public void Book_BudgetExhausted_PausesAndKeepsTheFile()
        {
            LogConfig cfg = Config();
            var book = new LogUploadRetryBook(cfg);
            var pending = new List<string> { BatchA };

            long now = 0L;
            for (int i = 1; i <= cfg.RetryMax; i++)
            {
                LogUploadRetryDecision step = book.Fail(BatchA, LogUploadOutcome.NetworkUnavailable, now, 0d);
                Assert.IsFalse(step.Paused, "attempt " + i + " should still retry");
                Assert.Greater(step.DelayMs, 0);
                now += step.DelayMs;
            }

            LogUploadRetryDecision last = book.Fail(BatchA, LogUploadOutcome.NetworkUnavailable, now, 0d);
            Assert.IsTrue(last.Paused);
            Assert.AreEqual(cfg.RetryMax + 1, last.Attempts);

            // 暂停之后：无论等多久都不会再被选中（但文件没被删，也没进黑名单）。
            int dueInMs;
            Assert.IsNull(book.NextSendable(pending, now + 3600L * 1000L, out dueInMs));
            Assert.IsFalse(book.IsSkipped(BatchA));
        }

        /// <summary>
        /// 钉"**网络恢复重置预算、暂停的批次重新可发**"。
        /// 没有这一条，断网超过半分钟的日志必然丢 —— 而那与"断网 2 分钟后能补传"的验收直接矛盾。
        /// </summary>
        [Test]
        public void Book_NetworkUnavailable_IsResetByNetworkResume()
        {
            LogConfig cfg = Config();
            var book = new LogUploadRetryBook(cfg);
            var pending = new List<string> { BatchA };

            for (int i = 0; i <= cfg.RetryMax; i++)
            {
                book.Fail(BatchA, LogUploadOutcome.NetworkUnavailable, 0L, 0d);
            }

            int dueInMs;
            Assert.IsNull(book.NextSendable(pending, 0L, out dueInMs), "should be paused before the resume");

            Assert.AreEqual(1, book.OnNetworkResumed());
            Assert.AreEqual(BatchA, book.NextSendable(pending, 0L, out dueInMs));
        }

        /// <summary>
        /// 钉"**服务端拒绝不因网络恢复而重置**"（specs/0004「必须区分两类失败」）。
        /// <para>
        /// 它和网络无关：一个 topic 配错、或者报文超限的批次，不该在每次网络抖动后
        /// 重新烧一轮预算 —— 它只应该靠 TTL 淘汰。
        /// </para>
        /// </summary>
        [Test]
        public void Book_ServerRejection_IsNotResetByNetworkResume()
        {
            LogConfig cfg = Config();
            var book = new LogUploadRetryBook(cfg);
            var pending = new List<string> { BatchA };

            for (int i = 0; i <= cfg.RetryMax; i++)
            {
                book.Fail(BatchA, LogUploadOutcome.ServerRejected, 0L, 0d);
            }

            Assert.AreEqual(0, book.OnNetworkResumed(), "a server rejection must not be reset by a network resume");

            int dueInMs;
            Assert.IsNull(book.NextSendable(pending, 0L, out dueInMs));
        }

        /// <summary>
        /// 钉**删除黑名单**：发送成功但文件删不掉时，该文件本次会话不再被返回。
        /// <para>
        /// 没有这一条，一个删不掉的文件会一直排在 FIFO 队首被无限重发、无限烧流量，
        /// 后面的批次一条都发不出去。下次启动内存态清零，会重试删除。
        /// </para>
        /// </summary>
        [Test]
        public void Book_DeleteFailure_SkipsFileForThisSession()
        {
            var book = new LogUploadRetryBook(Config());
            var pending = new List<string> { BatchA, BatchB };

            int dueInMs;
            Assert.AreEqual(BatchA, book.NextSendable(pending, 0L, out dueInMs));

            // 发送成功、但 LogOutbox.Delete 返回了 false。
            book.Skip(BatchA);

            Assert.IsTrue(book.IsSkipped(BatchA));
            Assert.AreEqual(BatchB, book.NextSendable(pending, 0L, out dueInMs),
                "the undeletable batch must not block the queue head");

            // 反复扫描也不会再把它交出来。
            Assert.AreEqual(BatchB, book.NextSendable(pending, 1000L, out dueInMs));
        }

        /// <summary>
        /// 钉 FIFO 顺序与"退避未到期就跳过"：队首在退避中时先发下一个，
        /// 并把"最近一个批次还要等多久"报给调用方，免得上传线程空转。
        /// </summary>
        [Test]
        public void Book_NextSendable_IsFifoAndRespectsBackoff()
        {
            var book = new LogUploadRetryBook(Config());
            var pending = new List<string> { BatchA, BatchB };

            int dueInMs;
            Assert.AreEqual(BatchA, book.NextSendable(pending, 0L, out dueInMs));
            Assert.AreEqual(0, dueInMs);

            LogUploadRetryDecision decision = book.Fail(BatchA, LogUploadOutcome.NetworkUnavailable, 0L, 0d);
            Assert.AreEqual(1000, decision.DelayMs);

            // A 在退避中 → 发 B（串行、一次一批，但队首不阻塞整条队列）。
            Assert.AreEqual(BatchB, book.NextSendable(pending, 0L, out dueInMs));

            // B 也退避掉之后，就只剩"还要等多久"这一个答案。
            book.Fail(BatchB, LogUploadOutcome.NetworkUnavailable, 0L, 0d);
            Assert.IsNull(book.NextSendable(pending, 100L, out dueInMs));
            Assert.AreEqual(900, dueInMs);

            // 到期后按 FIFO 回到 A。
            Assert.AreEqual(BatchA, book.NextSendable(pending, 1000L, out dueInMs));
        }

        /// <summary>
        /// 钉"**时钟 offset 变化立刻作废缓存的请求头与签名**"：
        /// 作废之后判定必须回到"需要重签名"。
        /// <para>
        /// 不作废的话会带着校正前的旧时间戳一路重试到预算耗尽 —— 旧签名对应的是校正前的
        /// 时间，必然继续落在 ±300 秒窗口外、必然继续失败。
        /// </para>
        /// <para>sha256 摘要**不作废**：它是 payload 派生的，与时间无关（协议 §4.4 的实现建议）。</para>
        /// </summary>
        [Test]
        public void Book_InvalidateSignatures_ForcesResignButKeepsTheDigest()
        {
            var book = new LogUploadRetryBook(Config());
            LogUploadBatchState state = book.State(BatchA);

            state.ShaHex = "4005c679d7ffd0ac9ff4d3557961780b2826e051eb889211a9c61f77f5abe4c6";
            state.TimestampText = "1756881000";
            state.Signature = "4228bcd1b4b20a1ae06a2f6982d705cfa9434bf44944baea25dfaeeb4acfe0a1";
            state.HasSignature = true;
            state.AnchorUptimeMs = 0L;

            // 作废之前：年龄还很小，本该原样重发。
            Assert.AreEqual(
                LogUploadSignPlan.Reuse,
                LogUploadPolicy.PlanSignature(state.HasSignature, 1000L, Config()));

            Assert.AreEqual(1, book.InvalidateSignatures());

            Assert.IsFalse(state.HasSignature);
            Assert.IsNull(state.TimestampText);
            Assert.IsNull(state.Signature);
            Assert.AreEqual(
                LogUploadSignPlan.Resign,
                LogUploadPolicy.PlanSignature(state.HasSignature, 1000L, Config()));

            Assert.AreEqual(
                "4005c679d7ffd0ac9ff4d3557961780b2826e051eb889211a9c61f77f5abe4c6",
                state.ShaHex,
                "the payload digest is not time-derived and must survive a clock change");
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  地址（协议 §4.2）—— 选址状态机是票 10 的，这里只钉"停发"与 URL 组装
        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 钉"**null / 空数组 = 停发**"（契约 C4）。停发只影响发送，不影响打包 ——
        /// 日志继续进 outbox 等地址恢复后补传。上传线程在这个状态下休眠，不做退避重试。
        /// </summary>
        [Test]
        public void Addresses_NullOrEmpty_MeansNoAddress()
        {
            var addresses = new LogUploadFirstAddress();
            Assert.IsFalse(addresses.HasAddress);

            addresses.Set(new[] { "silog-sh.example.com" });
            Assert.IsTrue(addresses.HasAddress);

            addresses.Set(null);
            Assert.IsFalse(addresses.HasAddress);

            addresses.Set(new string[0]);
            Assert.IsFalse(addresses.HasAddress);

            addresses.Set(new[] { "", "   " });
            Assert.IsFalse(addresses.HasAddress, "blank entries are not addresses");
        }

        /// <summary>
        /// 钉 URL 组装：只给 host 的补上批次接口路径与 https；已经带路径的原样用
        /// （协议 §4.2：地址以 dispatch 下发为准，可能是域名也可能是 IP，不做转换）。
        /// </summary>
        [Test]
        public void Addresses_BuildUrl_AppendsBatchPath()
        {
            Assert.AreEqual(
                "https://silog-sh.example.com/v1/logs/batch",
                LogUploadFirstAddress.BuildUrl("silog-sh.example.com"));

            Assert.AreEqual(
                "http://10.0.0.7:8080/v1/logs/batch",
                LogUploadFirstAddress.BuildUrl("http://10.0.0.7:8080"));

            Assert.AreEqual(
                "https://silog-sh.example.com/v1/logs/batch",
                LogUploadFirstAddress.BuildUrl("https://silog-sh.example.com/v1/logs/batch"));

            Assert.IsNull(LogUploadFirstAddress.BuildUrl(null));
            Assert.IsNull(LogUploadFirstAddress.BuildUrl("  "));
        }

        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 畸形 body 的统一断言：**没有 code、判失败、且带上了可排查的原因**。
        /// "判失败"意味着批次留在本地等下一轮，绝不会被删掉。
        /// </summary>
        private static void AssertMalformed(LogUploadResponse response)
        {
            Assert.IsFalse(response.HasCode, "a malformed body must not yield a usable code");
            Assert.IsFalse(response.IsSuccess, "a malformed body must never be treated as success");
            Assert.IsNotNull(response.Malformation);
        }
    }
}
