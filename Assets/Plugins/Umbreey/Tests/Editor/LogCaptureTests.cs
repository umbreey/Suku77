using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Umbreey.Tests
{
    /// <summary>
    /// 采集的 EditMode 测试（票 03 / specs 0001「采集」/ 全局硬约束 7）。
    ///
    /// <para>
    /// 本文件几乎全部在钉**一件事**：敏感哨兵的识别不能用 StartsWith。
    /// 这条机制一旦写错，表现是"完全失效，且没有任何报错"——
    /// 编译过、跑得动、日志照常产出，只是玩家的 token 会一路进到 CLS 里。
    /// 没有测试的话，这种错误只有在事故复盘时才会被发现。
    /// </para>
    /// <para>
    /// 之所以能在 EditMode 里测：<see cref="LogRecord.Capture"/> 是纯函数 ——
    /// 不碰 Unity API、不读全局状态、时间戳由调用方传入。
    /// </para>
    /// </summary>
    public class LogCaptureTests
    {
        /// <summary>
        /// Log.cs 的 Content() 拼在正文前的本地时间前缀，形如 "09/08 15:30:45.123: "。
        /// <para>
        /// 这里**照抄 Log.cs 的格式串**重新拼一遍，而不是写死一个字面量：
        /// 前缀的长度决定了敏感扫描窗口的下界，
        /// 哪天 Log.cs 改了格式，下面 <see cref="LogPrefix_IsExactly20Chars"/> 会立刻炸，
        /// 提醒有人回来重算窗口。
        /// </para>
        /// <para>
        /// ⚠️ 这只是**我们假设的**前缀，不是 Log.cs 实际产出的前缀：Log.cs 在 <c>.Sources/</c>
        /// 下（以 '.' 开头，Unity 忽略），真正编译进包的是同目录预编译的 Log.dll，
        /// EditMode 测不到它的真实行为。<see cref="LogRecord.SensitiveScanChars"/> 留的那段余量
        /// 正是为这个盲区买的保险。
        /// </para>
        /// </summary>
        private static string LogPrefix(DateTime time)
        {
            return string.Format(
                "{0:00}/{1:00} {2:00}:{3:00}:{4:00}.{5:000}: ",
                time.Month, time.Day, time.Hour, time.Minute, time.Second, time.Millisecond);
        }

        private static readonly string SamplePrefix = LogPrefix(new DateTime(2026, 9, 8, 15, 30, 45, 123));

        private static LogRecord Capture(string logString)
        {
            return LogRecord.Capture(0L, LogType.Log, logString, "", 0, false);
        }

        // ================================================================================
        //  窗口的下界：20 字符前缀 + 12 字符哨兵 = 32，而窗口必须比它更宽
        // ================================================================================

        /// <summary>
        /// 扫描窗口必须**容得下"带时间前缀的哨兵"并且还有余量**。
        ///
        /// <para>
        /// 下界 = 前缀长度 + 哨兵长度 = 20 + 12 = 32。窗口若恰好等于 32 就是零余量：
        /// 谁给 Log.cs 的前缀加一个等级标签、或给时间戳补上年份，已打标记的凭据日志
        /// 就**静默失去保护**（fail-open，没有任何报错，token 照常进 CLS）。
        /// 所以这条测试钉的不是等式，而是"**严格大于**下界" ——
        /// 有人把余量改回 0 时它会炸。
        /// </para>
        /// <para>
        /// 三个数字（前缀长度、哨兵长度、窗口）里任何一个变了都要回来重新推导。
        /// </para>
        /// </summary>
        [Test]
        public void LogPrefix_IsExactly20Chars()
        {
            Assert.AreEqual(20, SamplePrefix.Length, "Log.cs 的时间前缀长度变了，敏感扫描窗口需要重算");
            Assert.AreEqual(12, LogSystem.SensitiveTag.Length, "哨兵长度变了，敏感扫描窗口需要重算");

            int minimum = SamplePrefix.Length + LogSystem.SensitiveTag.Length;
            Assert.GreaterOrEqual(
                LogRecord.SensitiveScanChars,
                minimum,
                "扫描窗口必须容得下'带时间前缀的哨兵'，否则所有带前缀的敏感日志都会漏判");
            Assert.Greater(
                LogRecord.SensitiveScanChars,
                minimum,
                "窗口必须留有余量：贴边等于把'将来给 Log.cs 前缀加点东西'变成一次静默的凭据外泄");
        }

        // ================================================================================
        //  票 03「敏感标记识别」的 5 条必测项
        // ================================================================================

        /// <summary>
        /// 【1/5】带 Log.cs 时间戳前缀的字符串能命中。
        /// <para>
        /// **这条专防用 StartsWith 实现。** 哨兵位于第 20 个字符，
        /// 任何 StartsWith 写法在这里都会返回 false，凭据会照常上传。
        /// </para>
        /// </summary>
        [Test]
        public void Sensitive_HitsWhenPrecededByLogTimestampPrefix()
        {
            string raw = SamplePrefix + LogSystem.SensitiveTag + "sdk login status:0 token:abc123";

            LogRecord record = Capture(raw);

            Assert.IsTrue(record.Sensitive, "带 20 字符时间前缀时必须命中 —— 用 StartsWith 实现的话这里会挂");
            Assert.IsFalse(record.Msg.Contains(LogSystem.SensitiveTag));
            Assert.AreEqual(SamplePrefix + "sdk login status:0 token:abc123", record.Msg);
        }

        /// <summary>
        /// 【2/5】不带时间戳前缀的字符串（模拟第三方 SDK 的 Debug.Log）也能命中。
        /// <para>哨兵的位置不固定，正是它不能用固定位置判断的原因。</para>
        /// </summary>
        [Test]
        public void Sensitive_HitsWhenTagIsAtTheVeryBeginning()
        {
            string raw = LogSystem.SensitiveTag + "third party sdk token:xyz";

            LogRecord record = Capture(raw);

            Assert.IsTrue(record.Sensitive);
            Assert.AreEqual("third party sdk token:xyz", record.Msg);
        }

        /// <summary>
        /// 【3/5】剥离后 Msg 里不含标记，**其余内容一字不差**。
        /// <para>
        /// 剥离必须用 Remove(idx, len)：用 Substring(idx + len) 的话，
        /// 哨兵**前面**那段时间前缀会被一起切掉，本地卷里的时间戳就没了。
        /// </para>
        /// </summary>
        [Test]
        public void Sensitive_StripsOnlyTheTagAndKeepsEverythingElseVerbatim()
        {
            const string head = "abcdefgh";
            // 非 ASCII 写成 \uXXXX 转义而不是直接写汉字：它是断言的锚点，
            // 不能因为源文件被存成 GBK / 加了 BOM 之类的原因而悄悄变值（沿用 LogSignerTests 的约定）。
            const string tail = "tail with [brackets] and \u4e2d\u6587 and [#sensitive-ish] text";
            string raw = head + LogSystem.SensitiveTag + tail;

            LogRecord record = Capture(raw);

            Assert.IsTrue(record.Sensitive);
            Assert.AreEqual(head + tail, record.Msg, "除哨兵本身外，正文必须一字不差");
            Assert.AreEqual(raw.Length - LogSystem.SensitiveTag.Length, record.Msg.Length);
        }

        /// <summary>
        /// 【4/5】标记出现在扫描窗口**之后**的不算命中。
        /// <para>
        /// 窗口的意义是别让正文里偶然出现的同名字符串误伤 ——
        /// 比如某段日志正好在打印"我们用 [#sensitive] 这个前缀"这句话。
        /// </para>
        /// <para>
        /// 填充长度从 <see cref="LogRecord.SensitiveScanChars"/> 算出来，不写死 ——
        /// 窗口值再调整时这条测试自动跟上。
        /// </para>
        /// </summary>
        [Test]
        public void Sensitive_DoesNotHitWhenTagStartsBeyondTheScanWindow()
        {
            // 哨兵起点正好落在窗口外（第 SensitiveScanChars 个字符），最容易被 off-by-one 写错。
            string filler = new string('x', LogRecord.SensitiveScanChars);
            string raw = filler + LogSystem.SensitiveTag + "not a credential";

            LogRecord record = Capture(raw);

            Assert.IsFalse(record.Sensitive);
            Assert.AreEqual(raw, record.Msg, "未命中时正文必须原样保留，包括那串看起来像哨兵的文本");
        }

        /// <summary>
        /// 【4/5 补】哨兵**跨过**窗口边界（起点在窗口内、终点在窗口外）同样不算命中。
        /// <para>
        /// 语义是"哨兵完整落在前 <see cref="LogRecord.SensitiveScanChars"/> 个字符内"：
        /// 最后一个能命中的起点是 <c>window - tag.Length</c>，再往后挪一个字符就出界。
        /// 这条把"贴边命中 / 越界不中"这对边界一起钉住。
        /// </para>
        /// <para>
        /// 两个长度都从窗口常量反推，**不写死数字** —— 窗口从 32 调到 48 这类改动，
        /// 这条测试不需要跟着改。
        /// </para>
        /// </summary>
        [Test]
        public void Sensitive_BoundaryIsExactlyOneCharacterWide()
        {
            int lastHittingOffset = LogRecord.SensitiveScanChars - LogSystem.SensitiveTag.Length;

            string atBoundary = new string('x', lastHittingOffset) + LogSystem.SensitiveTag + "payload";
            string pastBoundary = new string('x', lastHittingOffset + 1) + LogSystem.SensitiveTag + "payload";

            Assert.IsTrue(
                Capture(atBoundary).Sensitive,
                "哨兵右端正好贴在窗口边界内侧时必须命中");
            Assert.IsFalse(
                Capture(pastBoundary).Sensitive,
                "哨兵右端越出窗口一个字符就不命中");
        }

        /// <summary>
        /// 【本次加固】**Log.cs 的前缀变长之后，已打标记的日志仍然命中。**
        ///
        /// <para>
        /// 这条是把 <see cref="LogRecord.SensitiveScanChars"/> 那 16 字符余量买到的东西钉下来。
        /// 现实场景：有人给 Log.cs 的 Content() 加一个等级标签（<c>"[I] "</c>），
        /// 或把时间戳补上年份（<c>"2026/09/08 15:30:45.123: "</c>）——
        /// 前缀从 20 涨到 24 / 28，四处已打标记的凭据日志若窗口仍是 32 就会**静默失去保护**。
        /// </para>
        /// <para>
        /// 之所以用假前缀而不是真的改 Log.cs：真前缀由预编译的 Log.dll 产出，测不到；
        /// 这里测的是"采集端对更长前缀的容忍度"，那才是本次加固的对象。
        /// </para>
        /// </summary>
        [Test]
        public void Sensitive_StillHitsWhenTheLogPrefixGrowsLonger()
        {
            // 给时间戳补上年份："2026/09/08 15:30:45.123: " —— 25 字符。
            string withYear = "2026/09/08 15:30:45.123: ";
            // 前缀再加一个等级标签："[I] " + 带年份的时间戳 —— 29 字符。
            string withYearAndLevel = "[I] " + withYear;

            foreach (string prefix in new[] { withYear, withYearAndLevel })
            {
                Assert.LessOrEqual(
                    prefix.Length + LogSystem.SensitiveTag.Length,
                    LogRecord.SensitiveScanChars,
                    "前置条件：这条测试要在窗口之内验证'更长的前缀'，否则它测的不是加固本身");

                string raw = prefix + LogSystem.SensitiveTag + "token:abc123";

                LogRecord record = Capture(raw);

                Assert.IsTrue(
                    record.Sensitive,
                    "前缀长度变成 " + prefix.Length + " 之后仍须命中 —— 窗口贴边的话这里会挂，"
                    + "而线上表现是凭据被静默上传到 CLS");
                Assert.AreEqual(prefix + "token:abc123", record.Msg, "只剥哨兵，前缀原样保留");
            }
        }

        /// <summary>
        /// 【5/5】命中的记录**进本地卷、不进批次累加器**；未命中的两边都进。
        /// <para>
        /// 这里只测"不进批次累加器"这一半（<see cref="LogSystem.AppendUploadable"/> 是
        /// 整个敏感机制唯一的策略点）。"照常进本地卷"那一半在
        /// <c>LogVolumeTests.Write_KeepsSensitiveRecordsInTheVolume</c> 里测 ——
        /// 那边要真的读文件，放在一起会让本文件被迫依赖磁盘。
        /// </para>
        /// </summary>
        [Test]
        public void Sensitive_IsExcludedFromTheBatchAccumulatorButPlainIsNot()
        {
            LogRecord sensitive = Capture(SamplePrefix + LogSystem.SensitiveTag + "token:abc123");
            LogRecord plain = Capture(SamplePrefix + "just a normal line");
            var source = new List<LogRecord> { sensitive, plain };

            var batch = new List<LogRecord>();
            int bytes = LogSystem.AppendUploadable(source, batch);

            Assert.AreEqual(1, batch.Count, "敏感记录不得进批次累加器");
            Assert.AreEqual(plain.Msg, batch[0].Msg);
            Assert.AreEqual(plain.Size, bytes, "返回的字节数只能计入真正进了累加器的那些");
        }

        // ================================================================================
        //  其余采集语义
        // ================================================================================

        /// <summary>
        /// 未命中的正文**原样保留**，尤其是 Log.cs 的时间前缀不剥离（契约 C1 / ADR-0002）。
        /// </summary>
        [Test]
        public void Capture_KeepsRawLogStringIncludingTheTimestampPrefix()
        {
            string raw = SamplePrefix + "[Main] Start";

            LogRecord record = Capture(raw);

            Assert.IsFalse(record.Sensitive);
            Assert.AreEqual(raw, record.Msg);
        }

        /// <summary>
        /// 空正文、null 正文都不能抛 —— 采集回调里抛异常等于把打日志的那个线程掀翻，
        /// 而那可能是主线程。
        /// </summary>
        [Test]
        public void Capture_TreatsNullStringsAsEmpty()
        {
            LogRecord record = LogRecord.Capture(0L, LogType.Log, null, null, 0, false);

            Assert.AreEqual("", record.Msg);
            Assert.AreEqual("", record.Stack);
            Assert.IsFalse(record.Sensitive);
        }

        /// <summary>
        /// 契约 C1：<see cref="LogRecord.Size"/> **必须包含堆栈的长度**。
        /// <para>
        /// 漏算堆栈的后果是异常刷屏时会话字节计数严重低估，
        /// "单卷不跨会话"（会话上限与单卷上限在参数总表里对齐）这条保证就失效了。
        /// </para>
        /// </summary>
        [Test]
        public void Size_IncludesTheStackLength()
        {
            string msg = new string('m', 100);
            string stack = new string('s', 500);

            int withoutStack = LogRecord.EstimateSize(msg, "");
            int withStack = LogRecord.EstimateSize(msg, stack);

            Assert.Greater(withStack, withoutStack);
            Assert.AreEqual(
                stack.Length, withStack - withoutStack, "堆栈要按它真实的 UTF-8 字节数计入");
        }

        /// <summary>
        /// <see cref="LogRecord.Size"/> **不得低于**实际 UTF-8 字节数。
        /// <para>
        /// 这是安全方向：低估会让"会话上限先于单卷上限触顶"反过来，
        /// 而那条对齐正是"一次会话永不跨卷"的全部依据。
        /// </para>
        /// </summary>
        [Test]
        public void Size_IsNeverBelowTheActualUtf8ByteCount()
        {
            string msg = new string('\u4e2d', 200);
            string stack = new string('\u6587', 300);

            int size = LogRecord.EstimateSize(msg, stack);
            int actual = System.Text.Encoding.UTF8.GetByteCount(msg)
                         + System.Text.Encoding.UTF8.GetByteCount(stack);

            Assert.GreaterOrEqual(size, actual);
        }

        /// <summary>
        /// <see cref="LogRecord.Size"/> 是**精确**的 UTF-8 字节数，不是 3×字符数 的上界。
        ///
        /// <para>
        /// 这条钉的是"同一把尺子"：参数总表把 SessionLimitBytes 与 VolumeMaxBytes
        /// 对齐到同一个数，而本地卷累加的是真实编码后写进去的字节。
        /// 用 3× 上界的话，纯 ASCII 日志下两把尺子差 3 倍 ——
        /// 会话会在卷才用掉三分之一时就停止记录，"对齐"只剩名义。
        /// </para>
        /// <para>
        /// 固定开销从 <c>EstimateSize("", "")</c> 反推，测试因此不必写死那个常量。
        /// </para>
        /// </summary>
        [Test]
        public void Size_MatchesTheActualUtf8ByteCountForBothAsciiAndCjk()
        {
            int overhead = LogRecord.EstimateSize("", "");

            string ascii = new string('a', 100);
            Assert.AreEqual(
                ascii.Length + overhead,
                LogRecord.EstimateSize(ascii, ""),
                "纯 ASCII 必须是 len + 开销，不是 3×len + 开销");

            // 汉字在 UTF-8 里是 3 字节/字符 —— 这一半保证改成精确之后没有反过来低估。
            string cjk = new string('\u4e2d', 100);
            Assert.AreEqual(
                cjk.Length * 3 + overhead,
                LogRecord.EstimateSize(cjk, ""),
                "汉字必须按 3 字节/字符 精确计入");

            // msg 与 stack 两段各自精确，合起来也精确。
            Assert.AreEqual(
                ascii.Length + cjk.Length * 3 + overhead,
                LogRecord.EstimateSize(ascii, cjk));
        }

        /// <summary>采集时记下的上下文版本号原样进记录（契约 C2，供 S3 切批）。</summary>
        [Test]
        public void Capture_CarriesTheContextVersion()
        {
            LogRecord record = LogRecord.Capture(0L, LogType.Log, "x", "", 42, false);

            Assert.AreEqual(42, record.CtxVer);
        }

        /// <summary>
        /// 时间戳与等级原样落进记录：等级不做映射（契约 C1），
        /// 时间戳是采集回调里取的设备原始时间（全局硬约束 5）。
        /// </summary>
        [Test]
        public void Capture_KeepsTimestampAndTypeVerbatim()
        {
            LogRecord record = LogRecord.Capture(1756881000123L, LogType.Exception, "boom", "at Foo()", 0, true);

            Assert.AreEqual(1756881000123L, record.TsUnixMs);
            Assert.AreEqual(LogType.Exception, record.Type);
            Assert.AreEqual("at Foo()", record.Stack);
            Assert.IsTrue(record.Internal, "系统自产的诊断日志要被标出来（全局硬约束 6）");
        }
    }
}
