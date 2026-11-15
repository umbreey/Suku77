using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Umbreey.Tests
{
    /// <summary>
    /// 选址状态机与 <see cref="LogClock"/> 的 EditMode 测试（票 10 / specs 0004 / 协议 §3.7 §4.2 §4.3）。
    ///
    /// <para>
    /// **网络本身不测**（价值低、脆弱）：<c>GET /v1/time</c> 走注入的
    /// <see cref="ILogServerTimeSource"/>，测的是限流、锚点、Epoch 与作废这几件决策。
    /// </para>
    ///
    /// <para>
    /// 这里钉的是三件"错了就会持续丢日志、而且现象会骗人"的事：
    /// <list type="number">
    ///   <item><b>锚点建立之后，墙上时钟（含 <c>TimeHelper</c> 的登录同步跳变）再怎么跳都不影响
    ///   <c>X-Timestamp</c></b> —— 早期 <c>TimeHelper + offset</c> 方案的必然 bug 就在这里：
    ///   基准被同步修正之后 offset 仍在叠加，请求反而被推出窗口，而"只校正首次"让它再也无法恢复。</item>
    ///   <item><b><c>logs[].ts</c> 在锚点存在时一个字节都不变</b> —— 这是本票最容易"好心做错"的地方。
    ///   <c>X-Timestamp</c> 被校正后永远落在 ±300 秒窗口内，服务端只剩 <c>rt - ts</c> 这条线索，
    ///   在客户端顺手修正 <c>ts</c> 会把证据一起抹掉（ADR-0002 末段）。</item>
    ///   <item><b>重锚之后立刻作废缓存的签名与请求头</b>，否则会带着校正前的旧签名一路重试到预算耗尽。</item>
    /// </list>
    /// </para>
    /// </summary>
    public class LogAddressClockTests
    {
        /// <summary>协议 §3.4 测试向量里的那一秒，方便和签名用例对读。</summary>
        private const long ServerNowSec = 1756881000L;

        /// <summary>设备比服务器慢 1 小时 —— 真机验收场景 C 就是这个偏差。</summary>
        private const long DeviceSlowBy = 3600L;

        private static LogConfig Config()
        {
            // 一律用参数总表的默认值（重锚最小间隔 = SuppressNoticeSec = 60s）。
            return new LogConfig();
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  地址选择状态机（协议 §4.2 / 票 10「地址选择」）
        //  票里点名的七条，一条一个用例，顺序与票一致。
        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>1/7 首次用第 0 个（协议 §4.2 第 1 条：优先用列表第一个）。</summary>
        [Test]
        public void Address_FirstUse_IsTheFirstEntry()
        {
            var book = new LogAddressBook();
            book.Set(new[] { "a.example.com", "b.example.com", "c.example.com" });

            Assert.IsTrue(book.HasAddress);
            Assert.AreEqual(0, book.Index);
            Assert.AreEqual("https://a.example.com/v1/logs/batch", book.Current);
            Assert.IsFalse(book.Sticky, "还没成功过就不该是粘住状态");
        }

        /// <summary>2/7 第 0 个失败 → 用第 1 个（协议 §4.2 第 2 条）。</summary>
        [Test]
        public void Address_FirstFails_MovesToTheSecond()
        {
            var book = new LogAddressBook();
            book.Set(new[] { "a.example.com", "b.example.com", "c.example.com" });

            book.ReportFailure();

            Assert.AreEqual(1, book.Index);
            Assert.AreEqual("https://b.example.com/v1/logs/batch", book.Current);
        }

        /// <summary>
        /// 3/7 第 1 个成功 → 后续一直用第 1 个（协议 §4.2 第 3 条：**成功后粘住本次会话**）。
        /// <para>
        /// 钉的是"**不要每批重新选**"：连续成功若干批之后下标仍然是 1，
        /// 而不是每次都回到列表头重新试一遍第 0 个（那会让每一批都先撞一次死地址）。
        /// </para>
        /// </summary>
        [Test]
        public void Address_SuccessSticksForTheSession()
        {
            var book = new LogAddressBook();
            book.Set(new[] { "a.example.com", "b.example.com", "c.example.com" });

            book.ReportFailure();
            book.ReportSuccess();

            Assert.IsTrue(book.Sticky);

            for (int batch = 0; batch < 10; batch++)
            {
                Assert.AreEqual("https://b.example.com/v1/logs/batch", book.Current, "batch " + batch);
                book.ReportSuccess();
            }

            Assert.AreEqual(1, book.Index);
        }

        /// <summary>
        /// 4/7 粘住后失败 → 换第 2 个。
        /// <para>
        /// ⚠️ 这一条与 <c>ConnectMod</c> 的网关锁定**刻意不同**：那边 locked 之后永不切换，
        /// 而日志上报没有"守着同一台重连"这回事 —— 票 10 明确要求粘住后失败也要换下一个。
        /// </para>
        /// </summary>
        [Test]
        public void Address_StickyThenFailure_MovesToTheNext()
        {
            var book = new LogAddressBook();
            book.Set(new[] { "a.example.com", "b.example.com", "c.example.com" });

            book.ReportFailure();
            book.ReportSuccess();
            Assert.IsTrue(book.Sticky);

            book.ReportFailure();

            Assert.AreEqual(2, book.Index);
            Assert.AreEqual("https://c.example.com/v1/logs/batch", book.Current);
            Assert.IsFalse(book.Sticky, "换了地址就不再是粘住状态");
        }

        /// <summary>5/7 全部失败 → 回到第 0 个，重新开始一轮（协议 §4.2 第 4 条）。</summary>
        [Test]
        public void Address_AllFail_WrapsBackToTheFirst()
        {
            var book = new LogAddressBook();
            book.Set(new[] { "a.example.com", "b.example.com", "c.example.com" });

            book.ReportFailure();
            book.ReportFailure();
            book.ReportFailure();

            Assert.AreEqual(0, book.Index);
            Assert.AreEqual("https://a.example.com/v1/logs/batch", book.Current);
            Assert.IsTrue(book.HasAddress, "走完一圈是重新开始一轮，不是进入无地址状态");
        }

        /// <summary>
        /// 6/7 空列表 / null → 进入「无地址」状态。
        /// <para>
        /// ⚠️ 这是**停发**（临时降级）：上传线程休眠、不做退避重试，
        /// 但打包继续、日志继续进 outbox 等地址恢复后补传，本地卷完全不受影响
        /// （本地卷根本不经过本类）。
        /// </para>
        /// </summary>
        [Test]
        public void Address_NullOrEmpty_EntersTheNoAddressState()
        {
            var book = new LogAddressBook();
            Assert.IsFalse(book.HasAddress, "dispatch 返回之前就是无地址");
            Assert.IsNull(book.Current);
            Assert.IsNull(book.CurrentTimeUrl);

            book.Set(new[] { "a.example.com" });
            Assert.IsTrue(book.HasAddress);

            book.Set(null);
            Assert.IsFalse(book.HasAddress);
            Assert.IsNull(book.Current);

            book.Set(new string[0]);
            Assert.IsFalse(book.HasAddress);

            book.Set(new[] { "", "   " });
            Assert.IsFalse(book.HasAddress, "空白项不是地址");
        }

        /// <summary>7/7 单元素列表：失败后仍然是它，**不越界**（<c>(0+1)%1 == 0</c>）。</summary>
        [Test]
        public void Address_SingleEntry_FailureStaysOnItWithoutGoingOutOfRange()
        {
            var book = new LogAddressBook();
            book.Set(new[] { "only.example.com" });

            for (int i = 0; i < 5; i++)
            {
                book.ReportFailure();

                Assert.AreEqual(0, book.Index);
                Assert.AreEqual("https://only.example.com/v1/logs/batch", book.Current);
                Assert.IsTrue(book.HasAddress, "唯一的地址失败了也不等于没有地址");
            }
        }

        /// <summary>
        /// 空白项被**逐项剔除**而不是让整张列表作废：dispatch 里混进一个空串，
        /// 不该把另外两个好地址一起废掉。
        /// </summary>
        [Test]
        public void Address_BlankEntriesAreDroppedNotTheWholeList()
        {
            var book = new LogAddressBook();
            book.Set(new[] { "", "a.example.com", "   ", "b.example.com" });

            Assert.AreEqual(2, book.Count);
            Assert.AreEqual("https://a.example.com/v1/logs/batch", book.Current);

            book.ReportFailure();
            Assert.AreEqual("https://b.example.com/v1/logs/batch", book.Current);
        }

        /// <summary>
        /// 同一份列表重复下发（dispatch 刷新 / 宿主重复调 <c>EnableUpload</c>）
        /// **不打断粘住** —— 否则每次刷新都要重新试一遍已知失败的地址。
        /// </summary>
        [Test]
        public void Address_SameListRedelivered_KeepsTheStickyChoice()
        {
            string[] addrs = { "a.example.com", "b.example.com" };

            var book = new LogAddressBook();
            book.Set(addrs);
            book.ReportFailure();
            book.ReportSuccess();

            book.Set(new[] { "a.example.com", "b.example.com" });

            Assert.AreEqual(1, book.Index, "同一份列表重复下发不该把下标打回第 0 个");
            Assert.IsTrue(book.Sticky);
        }

        /// <summary>
        /// 列表变了就是新一轮：回到第 0 个。
        /// <para>
        /// ⚠️ 顺带钉"**不缓存上次成功地址**"（票 10 的明确取舍）：停发再恢复之后，
        /// 选址从第 0 个重新开始，而不是记着上次成功的那个 —— 也没有任何内置兜底域名，
        /// 所以 <c>Set(null)</c> 之后是真的没有地址可用。
        /// </para>
        /// </summary>
        [Test]
        public void Address_NewList_RestartsFromTheFirstAndNothingIsCached()
        {
            var book = new LogAddressBook();
            book.Set(new[] { "a.example.com", "b.example.com" });
            book.ReportFailure();
            book.ReportSuccess();
            Assert.AreEqual(1, book.Index);

            // 停发：没有兜底域名、没有"上次成功地址"缓存，此刻真的发不出去。
            book.Set(null);
            Assert.IsFalse(book.HasAddress);
            Assert.IsNull(book.Current);

            book.Set(new[] { "a.example.com", "b.example.com" });
            Assert.AreEqual(0, book.Index, "恢复地址后从第 0 个重新开始，不复用上次成功的下标");
            Assert.IsFalse(book.Sticky);
        }

        /// <summary>
        /// URL 组装：批次接口与免签名的 <c>/v1/time</c> **来自同一条地址**（协议 §3.1 / §3.7）。
        /// <para>
        /// 地址可能是域名也可能是 IP，直接用、不做转换（协议 §4.2）；
        /// dispatch 直接下发完整批次 URL 时，<c>/v1/time</c> 要能从同一个前缀拼出来。
        /// </para>
        /// </summary>
        [Test]
        public void Address_BuildsBatchAndTimeUrlsFromTheSameEntry()
        {
            Assert.AreEqual(
                "https://silog-sh.example.com/v1/logs/batch",
                LogAddressBook.BuildBatchUrl("silog-sh.example.com"));
            Assert.AreEqual(
                "https://silog-sh.example.com/v1/time",
                LogAddressBook.BuildTimeUrl("silog-sh.example.com"));

            // IP 直连（内网）+ 显式 scheme 与端口。
            Assert.AreEqual(
                "http://10.0.0.7:8080/v1/logs/batch",
                LogAddressBook.BuildBatchUrl("http://10.0.0.7:8080"));
            Assert.AreEqual(
                "http://10.0.0.7:8080/v1/time",
                LogAddressBook.BuildTimeUrl("http://10.0.0.7:8080"));

            // dispatch 直接给完整 URL：批次原样用，/v1/time 摘掉批次路径再拼。
            Assert.AreEqual(
                "https://h.example.com/silog/v1/logs/batch",
                LogAddressBook.BuildBatchUrl("https://h.example.com/silog/v1/logs/batch"));
            Assert.AreEqual(
                "https://h.example.com/silog/v1/time",
                LogAddressBook.BuildTimeUrl("https://h.example.com/silog/v1/logs/batch"));

            // 末尾斜杠不该被误判成"带路径"。
            Assert.AreEqual(
                "https://h.example.com/v1/logs/batch",
                LogAddressBook.BuildBatchUrl("https://h.example.com/"));

            Assert.IsNull(LogAddressBook.BuildBatchUrl(null));
            Assert.IsNull(LogAddressBook.BuildTimeUrl("   "));

            // 残缺地址：只有 scheme 没有 host，不能拼出"看起来能用"的 URL。
            Assert.IsNull(LogAddressBook.BuildBatchUrl("https://"));
            Assert.IsNull(LogAddressBook.BuildTimeUrl("https:///"));

            // 接口层面：Current 与 CurrentTimeUrl 必须指向同一台服务器 ——
            // 校时问的必须是"正在拒绝我们的那一台"。
            var book = new LogAddressBook();
            book.Set(new[] { "a.example.com", "b.example.com" });
            book.ReportFailure();

            Assert.AreEqual("https://b.example.com/v1/logs/batch", book.Current);
            Assert.AreEqual("https://b.example.com/v1/time", book.CurrentTimeUrl);
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  换不换地址：节点级失败 vs 请求级失败（协议 §3.6 错误码表 + §4.2 规则 2）
        //
        //  钉的是"**这次失败说明了什么**"这个划分，而不是"有没有拿到 HTTP 响应"。
        //  后者会让客户端永远钉在一台过载的节点上，而列表里下一台是健康的。
        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// ⚠️ **<c>code=4</c>（过载或下游不可用）必须换下一个地址。**
        ///
        /// <para>
        /// 这是"拿到响应就说明这台是通的、所以不换"那种收窄实现的**最明确反例**：
        /// <c>code=4</c> 字面意思就是「这个节点过载」。不换的话，批次会在这台机器上
        /// 一路退避到预算耗尽然后暂停 —— 而协议 §4.2 规则 2 原文是
        /// 「连不上**或请求失败**，换下一个」，列表里下一台本来是健康的。
        /// </para>
        /// </summary>
        [Test]
        public void Address_Code4Overloaded_SwitchesToTheNextAddress()
        {
            var book = new LogAddressBook();
            book.Set(new[] { "a.example.com", "b.example.com", "c.example.com" });

            Assert.IsTrue(ApplyRejection(book, "{\"code\":4}"), "code=4 是节点级失败，必须换地址");
            Assert.AreEqual(1, book.Index);
            Assert.AreEqual("https://b.example.com/v1/logs/batch", book.Current);

            // 下一台也过载 → 继续往下走，不会钉在任何一台上。
            Assert.IsTrue(ApplyRejection(book, "{\"code\":4}"));
            Assert.AreEqual("https://c.example.com/v1/logs/batch", book.Current);
        }

        /// <summary>
        /// <c>code=3</c>（限流，很可能是单节点限流）与 <c>code=9</c>（内部错误，可能是这台的问题）
        /// 同样是节点级失败 → 换下一个。
        /// </summary>
        [Test]
        public void Address_Code3AndCode9_SwitchToTheNextAddress()
        {
            var book = new LogAddressBook();
            book.Set(new[] { "a.example.com", "b.example.com" });

            Assert.IsTrue(ApplyRejection(book, "{\"code\":3}"), "code=3 限流：换一台很可能就好了");
            Assert.AreEqual(1, book.Index);

            Assert.IsTrue(ApplyRejection(book, "{\"code\":9}"), "code=9 内部错误：可能是这台的问题");
            Assert.AreEqual(0, book.Index, "两台都失败 → 回到第 0 个重新开始一轮");

            // 没拿到 HTTP 响应（连接层错误 / TLS / 超时）也是节点级失败。
            LogUploadResponse none;
            Assert.IsTrue(LogUploadPolicy.ShouldSwitchAddress(
                LogUploadPolicy.Classify(
                    LogUploadTransportResult.NoResponse("SocketException: timed out"), out none),
                none));
        }

        /// <summary>
        /// <c>code=1</c>（报文格式错误 / 超限）与 <c>code=2</c>（签名无效 / appId 不存在）
        /// 是**请求级**失败：换哪台都是同一个结果，**不换地址**。
        /// <para>
        /// <c>code=5</c>（时间窗超限）同理 —— 它另有一条正交的处理（<c>/v1/time</c> 重锚），
        /// 但同样不该动地址。畸形 body 也归这一类：协议对不上，换址不解决。
        /// </para>
        /// </summary>
        [Test]
        public void Address_RequestLevelCodes_KeepTheCurrentAddress()
        {
            var book = new LogAddressBook();
            book.Set(new[] { "a.example.com", "b.example.com", "c.example.com" });

            string[] requestLevel = { "{\"code\":1}", "{\"code\":2}", "{\"code\":5}" };

            for (int i = 0; i < requestLevel.Length; i++)
            {
                Assert.IsFalse(ApplyRejection(book, requestLevel[i]), requestLevel[i] + " 不该触发换址");
                Assert.AreEqual(0, book.Index, requestLevel[i] + " 之后仍应用当前地址");
                Assert.AreEqual("https://a.example.com/v1/logs/batch", book.Current);
            }

            // 畸形 body（被网关换成 HTML、code 缺失、code 是字符串）同样不换。
            Assert.IsFalse(ApplyRejection(book, "{}"));
            Assert.IsFalse(ApplyRejection(book, "{\"code\":\"4\"}"), "字符串 \"4\" 不是节点级信号");
            Assert.IsFalse(ApplyRejection(book, "<html>502 Bad Gateway</html>"));
            Assert.AreEqual(0, book.Index);
        }

        /// <summary>
        /// 未来新增的**未知 code** → **保守不换**。
        /// <para>
        /// 不认识的东西不要当成节点故障：否则服务端加一个新错误码，
        /// 客户端就会把整个地址列表轮一遍。
        /// </para>
        /// </summary>
        [Test]
        public void Address_UnknownCode_KeepsTheCurrentAddressConservatively()
        {
            var book = new LogAddressBook();
            book.Set(new[] { "a.example.com", "b.example.com" });

            Assert.IsFalse(ApplyRejection(book, "{\"code\":77}"), "不认识的 code 不当成节点故障");
            Assert.AreEqual(0, book.Index);
            Assert.AreEqual("https://a.example.com/v1/logs/batch", book.Current);

            Assert.IsFalse(ApplyRejection(book, "{\"code\":-1}"));
            Assert.AreEqual(0, book.Index);
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  LogClock（票 10「时钟校正」/ specs 0004「时钟」/ ADR-0002 末段）
        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 无锚点 → 回落到设备墙上时钟（协议 §4.3 明确允许"什么都不做"）。
        /// </summary>
        [Test]
        public void Clock_WithoutAnchor_UsesTheDeviceClock()
        {
            long device = ServerNowSec - DeviceSlowBy;
            var clock = new LogClock(Config(), new FakeTimeSource(), () => device);

            Assert.IsFalse(clock.HasAnchor);
            Assert.AreEqual(device, clock.NowUnixSec(0L));
            Assert.AreEqual(0, clock.Epoch);
        }

        /// <summary>
        /// 有锚点 → <c>serverUnixSec + 单调经过秒数</c>，且落在服务端 ±300 秒窗口内。
        /// </summary>
        [Test]
        public void Clock_Anchored_IsServerTimePlusMonotonicElapsed()
        {
            long device = ServerNowSec - DeviceSlowBy;
            var clock = new LogClock(Config(), new FakeTimeSource(), () => device);

            clock.SetAnchor(ServerNowSec, 5000L);

            Assert.AreEqual(ServerNowSec, clock.NowUnixSec(5000L));
            Assert.AreEqual(ServerNowSec + 2L, clock.NowUnixSec(7500L), "经过 2.5 秒按秒取整是 +2");
            Assert.AreEqual(ServerNowSec + 60L, clock.NowUnixSec(65000L));

            // 「落在窗口内」的直接断言：锚点建立 30 秒后，和服务器真实时间只差 30 秒。
            long serverRealNow = ServerNowSec + 30L;
            Assert.LessOrEqual(Math.Abs(clock.NowUnixSec(35000L) - serverRealNow), 300L);

            // 对照：没有校正的话此刻会差 1 小时，远在窗口之外 —— 那就是 code=5。
            Assert.Greater(Math.Abs(device + 30L - serverRealNow), 300L);
        }

        /// <summary>
        /// 锚点建立之后，**设备墙上时钟跳变不影响** <c>NowUnixSec()</c>。
        /// <para>
        /// 玩家中途再改一次系统时间、系统 NTP 校准都属于这一类。
        /// 经过时间只从单调时钟来，所以墙上时钟怎么跳都进不到公式里。
        /// </para>
        /// </summary>
        [Test]
        public void Clock_Anchored_IgnoresDeviceWallClockJumps()
        {
            long device = ServerNowSec - DeviceSlowBy;
            var clock = new LogClock(Config(), new FakeTimeSource(), () => device);

            clock.SetAnchor(ServerNowSec, 1000L);
            long before = clock.NowUnixSec(1000L);

            device += 7200L;                                  // 玩家把时间往前调 2 小时
            Assert.AreEqual(before, clock.NowUnixSec(1000L));

            device -= 100000L;                                // 再往后调一大截
            Assert.AreEqual(before, clock.NowUnixSec(1000L));
        }

        /// <summary>
        /// ⚠️ **专测早期方案那个必然发生的 bug**：模拟 <c>TimeHelper</c> 在锚点建立**之后**
        /// 发生登录同步跳变，<c>NowUnixSec()</c> 必须完全不受影响。
        ///
        /// <para>
        /// 早期方案是 <c>TimeHelper.UtcTimestamp + offset</c>：
        /// 设备慢 1 小时 → 登录前 <c>code=5</c> → 算出 <c>offset=+3600</c> → 请求恢复正常；
        /// 登录成功后 <c>TimeHelper</c> 被服务器时间同步、**基准本身变正确了**，
        /// 而 offset 仍在叠加 → 请求反而快 1 小时 → 再次超窗失败；
        /// 「只校正首次 <c>code=5</c>」让它**再也无法恢复**。
        /// </para>
        /// <para>
        /// <see cref="LogClock"/> 全文一处都不读 <c>TimeHelper</c>，所以这里的跳变对它是不存在的事件。
        /// 用例特意先断言"跳变确实发生了"（<c>TimeHelper.UtcTimestamp</c> 真的变了），
        /// 否则这条用例可能什么都没证明。
        /// </para>
        /// </summary>
        [Test]
        public void Clock_Anchored_IsUnaffectedByTimeHelperSyncJump()
        {
            long device = ServerNowSec - DeviceSlowBy;
            var clock = new LogClock(Config(), new FakeTimeSource(), () => device);

            clock.SetAnchor(ServerNowSec, 1000L);
            long before = clock.NowUnixSec(1000L);
            int epochBefore = clock.Epoch;

            int helperBefore = TimeHelper.UtcTimestamp;

            try
            {
                // 登录成功，服务器时间下来了 —— TimeHelper 的基准整体跳一小时。
                TimeHelper.Sync(helperBefore + DeviceSlowBy);
                Assert.AreNotEqual(
                    helperBefore,
                    TimeHelper.UtcTimestamp,
                    "夹具本身没造成跳变，这条用例就什么都没证明");

                Assert.AreEqual(before, clock.NowUnixSec(1000L),
                    "LogClock 读了 TimeHelper：登录同步一发生，X-Timestamp 就会被推出窗口");

                // 反方向再跳一次（服务端换机、跨天漂移）。
                TimeHelper.Sync(helperBefore - 2L * DeviceSlowBy);
                Assert.AreEqual(before, clock.NowUnixSec(1000L));

                Assert.AreEqual(epochBefore, clock.Epoch, "TimeHelper 的跳变不是锚点变化，不该作废签名");
            }
            finally
            {
                // 还原，免得污染同一个域里后续任何用到 TimeHelper 的用例。
                TimeHelper.Sync(UnixNowSec());
            }
        }

        /// <summary>
        /// ⚠️ **第二次 <c>code=5</c> 能重设锚点；最小间隔内的重复请求被限流挡掉。**
        ///
        /// <para>
        /// 两件事都是硬要求，方向相反：
        /// </para>
        /// <list type="bullet">
        ///   <item>"只校正首次"是早期方案再也无法恢复的直接原因 → 必须能重锚**多次**；</item>
        ///   <item>服务端持续返回 <c>code=5</c> 时，每个批次的每次重试都会走到重锚
        ///   → 必须有最小间隔，否则 <c>/v1/time</c> 会被打成一条压测流量。</item>
        /// </list>
        /// </summary>
        [Test]
        public void Clock_Resync_SecondCodeFiveReanchors_ButIsRateLimitedWithinTheInterval()
        {
            LogConfig cfg = Config();
            var source = new FakeTimeSource { ServerUnixSec = ServerNowSec };
            var clock = new LogClock(cfg, source, () => ServerNowSec - DeviceSlowBy);

            long interval = clock.MinResyncIntervalMs;
            Assert.AreEqual(cfg.SuppressNoticeSec * 1000L, interval, "最小间隔复用 SuppressNoticeSec(60s)");

            // 第一次 code=5：立刻重锚（0 时刻也是合法的单调时刻，不能被当成"没试过"吃掉）。
            Assert.AreEqual(LogClockResync.Anchored, Resync(clock, 0L));
            Assert.AreEqual(1, source.Calls);
            Assert.AreEqual(1, clock.Epoch);

            // 间隔内的重复请求：一律挡掉，**一次网络请求都不发**。
            Assert.AreEqual(LogClockResync.Throttled, Resync(clock, 1L));
            Assert.AreEqual(LogClockResync.Throttled, Resync(clock, interval / 2L));
            Assert.AreEqual(LogClockResync.Throttled, Resync(clock, interval - 1L));
            Assert.AreEqual(1, source.Calls, "限流期间不得再发 /v1/time");
            Assert.AreEqual(1, clock.Epoch, "被挡掉的重锚不该作废任何签名");

            // 跨过最小间隔：第二次 code=5 能重设锚点（**不是只做一次**）。
            source.ServerUnixSec = ServerNowSec + 600L;
            Assert.AreEqual(LogClockResync.Anchored, Resync(clock, interval));
            Assert.AreEqual(2, source.Calls);
            Assert.AreEqual(2, clock.Epoch);
            Assert.AreEqual(ServerNowSec + 600L, clock.NowUnixSec(interval), "锚点应当换成第二次拿到的时间");
        }

        /// <summary>
        /// <c>/v1/time</c> 拿不到可用时间时：**锚点保持原样**，且这次尝试**照样占用最小间隔**。
        /// <para>
        /// 不占用的话，服务端 502 时每次 <c>code=5</c> 都会重试一发校时，限流形同虚设。
        /// 保留旧锚点也比拿垃圾值覆盖它安全得多。
        /// </para>
        /// </summary>
        [Test]
        public void Clock_Resync_ProbeFailure_KeepsTheAnchorAndStillConsumesTheInterval()
        {
            var source = new FakeTimeSource { ServerUnixSec = ServerNowSec };
            var clock = new LogClock(Config(), source, () => ServerNowSec - DeviceSlowBy);

            Assert.AreEqual(LogClockResync.Anchored, Resync(clock, 0L));
            long interval = clock.MinResyncIntervalMs;

            source.Available = false;
            source.Error = "SocketException: timed out";

            Assert.AreEqual(LogClockResync.Failed, Resync(clock, interval));
            Assert.AreEqual(2, source.Calls);
            Assert.AreEqual(1, clock.Epoch, "失败不该递增 Epoch —— 没有新锚点，就没有需要作废的签名");
            Assert.AreEqual(ServerNowSec, clock.NowUnixSec(0L), "失败时旧锚点必须原样保留");
            Assert.AreEqual("SocketException: timed out", clock.LastError);

            // 失败也占额度：紧接着的一次仍然被限流挡掉。
            Assert.AreEqual(LogClockResync.Throttled, Resync(clock, interval + 1L));
            Assert.AreEqual(2, source.Calls);
        }

        /// <summary>
        /// 没有 <c>/v1/time</c> 执行者（WebGL：连传输层都没有）时不校时，也不报错。
        /// </summary>
        [Test]
        public void Clock_Resync_WithoutASource_IsDisabled()
        {
            var clock = new LogClock(Config(), null, () => ServerNowSec);

            Assert.AreEqual(LogClockResync.Disabled, Resync(clock, 0L));
            Assert.IsFalse(clock.HasAnchor);
            Assert.AreEqual(0, clock.Epoch);

            // 票 09 的半成品同样是"不校时"这一档，语义对齐。
            Assert.AreEqual(
                LogClockResync.Disabled,
                LogUploadDeviceClock.Instance.OnTimestampRejected("https://h/v1/time", 0L, CancellationToken.None));
        }

        /// <summary>
        /// <c>/v1/time</c> 响应解析（协议 §3.7）。
        /// <para>
        /// ⚠️ 判定逐层收紧：把一个垃圾值当成服务器时间，会让**之后所有请求**都带着错误的
        /// <c>X-Timestamp</c>，那比"这次没校时"坏得多。所以字符串 <c>"175..."</c>、浮点、
        /// <c>null</c>、非 0 的 <c>code</c>、非对象、非 JSON 全部拒掉。
        /// </para>
        /// </summary>
        [Test]
        public void Clock_ParseServerTime_OnlyAcceptsAPositiveJsonInteger()
        {
            long sec;

            Assert.IsTrue(LogClock.TryParseServerTime("{\"code\":0,\"server_time\":1756881000}", out sec));
            Assert.AreEqual(ServerNowSec, sec);

            // code 字段可以缺省（协议示例里带，但我们只关心 server_time 本身）。
            Assert.IsTrue(LogClock.TryParseServerTime("{\"server_time\":1756881000}", out sec));
            Assert.AreEqual(ServerNowSec, sec);

            // 服务端自己说这次不算成功 → 不能用它的时间。
            Assert.IsFalse(LogClock.TryParseServerTime("{\"code\":9,\"server_time\":1756881000}", out sec));

            Assert.IsFalse(LogClock.TryParseServerTime("{\"server_time\":\"1756881000\"}", out sec), "字符串");
            Assert.IsFalse(LogClock.TryParseServerTime("{\"server_time\":1756881000.5}", out sec), "浮点");
            Assert.IsFalse(LogClock.TryParseServerTime("{\"server_time\":null}", out sec), "null");
            Assert.IsFalse(LogClock.TryParseServerTime("{\"server_time\":0}", out sec), "0 不是时间");
            Assert.IsFalse(LogClock.TryParseServerTime("{\"server_time\":-1}", out sec), "负数不是时间");
            Assert.IsFalse(LogClock.TryParseServerTime("{\"code\":0}", out sec), "缺字段");
            Assert.IsFalse(LogClock.TryParseServerTime("{}", out sec));
            Assert.IsFalse(LogClock.TryParseServerTime("1756881000", out sec), "顶层不是对象");
            Assert.IsFalse(LogClock.TryParseServerTime("<html>502</html>", out sec), "被网关换成了 HTML");
            Assert.IsFalse(LogClock.TryParseServerTime("{\"server_time\":175", out sec), "截断");
            Assert.IsFalse(LogClock.TryParseServerTime("", out sec));
            Assert.IsFalse(LogClock.TryParseServerTime(null, out sec));
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  锚点更新 → 作废缓存的签名与请求头（票 10 / specs 0004「重签名窗口」末条）
        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// ⚠️ **锚点更新后，缓存的签名与请求头立刻被作废**，而 sha256 摘要保留。
        ///
        /// <para>
        /// 这条走的是真实链路：<see cref="LogClock"/> 重锚 → <see cref="LogClock.Epoch"/> 递增 →
        /// <c>LogUploader.ApplyClockEpoch()</c> → <c>LogUploadRetryBook.InvalidateSignatures()</c>。
        /// 不作废的话会带着**校正前**的旧签名一路重试到预算耗尽 ——
        /// 旧时间戳必然继续落在 ±300 秒窗口外，必然继续 <c>code=5</c>，校时白做。
        /// </para>
        /// <para>
        /// 摘要必须活下来：它是 payload 派生的，与时间无关（协议 §4.4 的实现建议）。
        /// </para>
        /// </summary>
        [Test]
        public void Uploader_ClockReanchor_InvalidatesCachedSignaturesButKeepsTheDigest()
        {
            const string name = "0000001000-aaaaaaaa-00001.json";
            const string sha = "4005c679d7ffd0ac9ff4d3557961780b2826e051eb889211a9c61f77f5abe4c6";

            LogConfig cfg = Config();
            var source = new FakeTimeSource { ServerUnixSec = ServerNowSec };
            var clock = new LogClock(cfg, source, () => ServerNowSec - DeviceSlowBy);

            // transport 传 null：本用例只驱动决策，不发任何请求（线程也没起）。
            var uploader = new LogUploader(cfg, new LogAddressBook(), clock, null);

            LogUploadBatchState state = uploader.Book.State(name);
            state.ShaHex = sha;
            state.TimestampText = "1756881000";
            state.Signature = "4228bcd1b4b20a1ae06a2f6982d705cfa9434bf44944baea25dfaeeb4acfe0a1";
            state.HasSignature = true;

            Assert.AreEqual(0, uploader.ApplyClockEpoch(), "时钟没变就什么都不该作废");

            Assert.AreEqual(LogClockResync.Anchored, Resync(clock, 0L));

            Assert.AreEqual(1, uploader.ApplyClockEpoch());
            Assert.IsFalse(state.HasSignature, "锚点变了，缓存的签名必须失效");
            Assert.IsNull(state.TimestampText, "旧的 X-Timestamp 必须一起丢掉");
            Assert.IsNull(state.Signature);
            Assert.AreEqual(sha, state.ShaHex, "sha256 是 payload 派生的，与时钟无关，必须保留");

            // 作废之后的下一次发送一定重新签名。
            Assert.AreEqual(
                LogUploadSignPlan.Resign,
                LogUploadPolicy.PlanSignature(state.HasSignature, 0L, cfg));

            // 幂等：同一个 Epoch 不会被作废两次。
            Assert.AreEqual(0, uploader.ApplyClockEpoch());
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  ⚠️ 本票的核心风险点：logs[].ts 绝不使用 LogClock
        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// ⚠️ **锚点存在时，<c>logs[].ts</c> 保持不变**（全局硬约束 5 / ADR-0002 末段）。
        ///
        /// <para>
        /// 这是本票最容易"好心做错"的地方，所以单独一条用例：
        /// <c>X-Timestamp</c> 一旦被校正就永远落在服务端 ±300 秒窗口内，
        /// 服务端再也无法从请求头识别时钟异常的设备，此时 <c>rt - ts</c> 是**仅剩的线索**
        /// （注意它是线索不是结论 —— 离线积压补传、网络延迟都会造成同样的偏大，
        /// 需要结合"该 client_id 是否出现过 code=5"一起判断）。
        /// 在客户端顺手修正 <c>ts</c> 会把这个证据一起抹掉。
        /// </para>
        /// <para>
        /// 断言的形态是"两个时间必须不一致"：设备慢 1 小时、时钟已按服务器时间重锚，
        /// 此时 <c>ts</c> 仍然是**偏差后的设备时间**（真机验收场景 C 的最后一条），
        /// 而 <c>X-Timestamp</c> 是校正后的时间。
        /// </para>
        /// </summary>
        [Test]
        public void BatchTs_StaysTheRawDeviceTime_EvenWhenTheClockIsAnchored()
        {
            long deviceNowSec = ServerNowSec - DeviceSlowBy;
            long deviceTsMs = deviceNowSec * 1000L + 123L;      // 采集回调里 DateTime.UtcNow 的原值

            var source = new FakeTimeSource { ServerUnixSec = ServerNowSec };
            var clock = new LogClock(Config(), source, () => deviceNowSec);
            Assert.AreEqual(LogClockResync.Anchored, Resync(clock, 0L));

            // X-Timestamp 走 LogClock（已校正），而 ts 走记录里的原始值。
            long xTimestamp = clock.NowUnixSec(0L);
            Assert.AreEqual(ServerNowSec, xTimestamp, "X-Timestamp 应当已经被校正到服务器时间");

            byte[] payloadRaw = LogBatch.Serialize(
                new List<LogRecord>
                {
                    new LogRecord
                    {
                        TsUnixMs = deviceTsMs,
                        Type = LogType.Log,
                        Msg = "connect gateway",
                        Stack = "",
                        CtxVer = 1,
                    },
                },
                new LogContextSnapshot(1, "10086", "prod", "1.2.5", "taptap", "1.2.3",
                    "android", "Pixel 8", "Adreno", "Android 14", "cid-1", "trace-1"),
                "game_a",
                "client",
                Config());

            JObject root = JObject.Parse(Encoding.UTF8.GetString(payloadRaw));
            long ts = root["logs"][0]["ts"].Value<long>();

            Assert.AreEqual(deviceTsMs, ts, "logs[].ts 被时钟校正污染了 —— rt - ts 这条线索会被抹掉");

            // 证据还在：ts 与校正后的 X-Timestamp 差着整整一个偏差（这正是服务端要看到的）。
            Assert.AreEqual(DeviceSlowBy, xTimestamp - ts / 1000L);
            Assert.Greater(Math.Abs(xTimestamp - ts / 1000L), 300L,
                "如果 ts 也被校正，它就会落在 ±300 秒窗口内，服务端再也看不出这台设备时钟不准");
        }

        // ════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 走**真实判定链**把一次"服务端拒绝"作用到选址上：
        /// <c>Classify</c>（只看 body 的 code）→ <c>ShouldSwitchAddress</c>（节点级 / 请求级）
        /// → <c>ReportFailure</c>。与 <c>LogUploader.OnFailure</c> 里的那三行同形，
        /// 所以这几条用例钉的是生产代码的划分，不是测试自己又写了一遍规则。
        /// </summary>
        /// <returns>是否换了地址。</returns>
        private static bool ApplyRejection(LogAddressBook book, string body)
        {
            LogUploadResponse response;
            LogUploadOutcome outcome = LogUploadPolicy.Classify(
                LogUploadTransportResult.Responded(200, body), out response);

            Assert.AreNotEqual(LogUploadOutcome.Success, outcome, "夹具本身搞错了：这不是一次失败");

            bool switched = LogUploadPolicy.ShouldSwitchAddress(outcome, response);
            if (switched)
            {
                book.ReportFailure();
            }

            return switched;
        }

        private static LogClockResync Resync(LogClock clock, long nowMs)
        {
            return clock.OnTimestampRejected("https://silog-sh.example.com/v1/time", nowMs, CancellationToken.None);
        }

        private static long UnixNowSec()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        /// <summary>
        /// <c>GET /v1/time</c> 的替身。**不碰网络** —— 测的是限流、锚点与作废这几件决策
        /// （与票 09 用 <c>ILogUploadTransport</c> 替身测判定同一个做法）。
        /// </summary>
        private sealed class FakeTimeSource : ILogServerTimeSource
        {
            internal long ServerUnixSec = 0L;
            internal bool Available = true;
            internal string Error;
            internal int Calls;
            internal string LastUrl;

            public bool TryGetServerUnixSec(
                string timeUrl, CancellationToken token, out long serverUnixSec, out string error)
            {
                Calls++;
                LastUrl = timeUrl;

                if (!Available)
                {
                    serverUnixSec = 0L;
                    error = Error;
                    return false;
                }

                serverUnixSec = ServerUnixSec;
                error = null;
                return true;
            }
        }
    }
}
