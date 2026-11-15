using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Umbreey.Tests
{
    /// <summary>
    /// LogBatch 的 EditMode 测试（票 07 / specs 0003 的序列化部分）。
    ///
    /// <para>
    /// 外部行为 = **给定记录和上下文，产出的字节序列**。所以断言尽量直接打在
    /// payload_raw 的文本上，而不是打在反序列化之后的对象上 —— 后者区分不出
    /// 「空串」和「缺失」，而协议恰恰要求"必填字段拿不到值时传空串、不省略"。
    /// Newtonsoft 的反向解析只用来证明"这堆字节是合法 JSON"，**只在测试代码里用**。
    /// </para>
    /// </summary>
    public class LogBatchTests
    {
        private const string AppId = "game_a";
        private const string Topic = "client";

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
        }

        // ================================================================================
        //  夹具
        // ================================================================================

        /// <summary>
        /// 造一个上下文快照。默认值刻意都是空串 —— "启动早期什么都还没就绪"
        /// 才是这套系统最需要跑对的场景。
        /// </summary>
        private static LogContextSnapshot Ctx(
            int version,
            string uid = "",
            string env = "",
            string resVersion = "",
            string channel = "",
            string appVersion = "",
            string platform = "",
            string device = "",
            string gpu = "",
            string system = "",
            string clientId = "",
            string traceId = "")
        {
            return new LogContextSnapshot(
                version, uid, env, resVersion, channel, appVersion,
                platform, device, gpu, system, clientId, traceId);
        }

        /// <summary>协议 §3.5 示例那种"全字段都有值"的快照，用来钉批头的完整形态。</summary>
        private static LogContextSnapshot FullCtx(int version = 7)
        {
            return Ctx(
                version,
                uid: "10086",
                env: "prod",
                resVersion: "1.2.5",
                channel: "taptap",
                appVersion: "1.2.3",
                platform: "android",
                device: "Pixel 8",
                gpu: "Adreno",
                system: "Android 14",
                clientId: "cid-1",
                traceId: "trace-1");
        }

        private static LogRecord Rec(
            string msg,
            string stack = "",
            long ts = 1756881000123L,
            LogType type = LogType.Log,
            int ctxVer = 7,
            bool sensitive = false,
            bool isInternal = false,
            int size = 0)
        {
            return new LogRecord
            {
                TsUnixMs = ts,
                Type = type,
                Msg = msg,
                Stack = stack,
                Size = size,
                CtxVer = ctxVer,
                Internal = isInternal,
                Sensitive = sensitive,
            };
        }

        private static Func<int, LogContextSnapshot> Fixed(LogContextSnapshot ctx)
        {
            return _ => ctx;
        }

        /// <summary>把一批记录序列化成 payload_raw 的文本形态（测试断言的主要对象）。</summary>
        private static string Text(byte[] payloadRaw)
        {
            return Encoding.UTF8.GetString(payloadRaw);
        }

        private static byte[] One(LogRecord record, LogContextSnapshot ctx, LogConfig cfg = null)
        {
            List<LogBatchPayload> batches = LogBatch.Build(
                new List<LogRecord> { record }, Fixed(ctx), AppId, Topic, cfg);
            Assert.AreEqual(1, batches.Count, "夹具本身应当只产出一个批次");
            return batches[0].Bytes;
        }

        private static string OneText(LogRecord record, LogContextSnapshot ctx, LogConfig cfg = null)
        {
            return Text(One(record, ctx, cfg));
        }

        /// <summary>从 payload 文本里取出 msg 字段的值（反向解析，仅测试用）。</summary>
        private static string MsgOf(string payloadText)
        {
            JObject root = JsonConvert.DeserializeObject<JObject>(payloadText);
            return (string)root["logs"][0]["msg"];
        }

        private static string StackOf(string payloadText)
        {
            JObject root = JsonConvert.DeserializeObject<JObject>(payloadText);
            return (string)root["logs"][0]["fields"]["stack"];
        }

        private static int Utf8Len(string value)
        {
            return Encoding.UTF8.GetByteCount(value);
        }

        private static string Repeat(char c, int count)
        {
            return new string(c, count);
        }

        /// <summary>
        /// 断言 <paramref name="needle"/> 作为**连续字节序列**出现在 payload 里。
        /// <para>
        /// 刻意不用 CollectionAssert.IsSubsetOf —— 那是集合语义（"每个字节值都出现过"），
        /// 而这里要证明的是"这几个字节挨在一起"，比如 emoji 的 4 字节 UTF-8 序列没被拆散。
        /// </para>
        /// </summary>
        private static void AssertContainsBytes(byte[] needle, byte[] payload, string message)
        {
            for (int i = 0; i + needle.Length <= payload.Length; i++)
            {
                bool hit = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (payload[i + j] != needle[j])
                    {
                        hit = false;
                        break;
                    }
                }

                if (hit)
                {
                    return;
                }
            }

            Assert.Fail(message);
        }

        // ================================================================================
        //  1. 批头 —— 字段齐全、顺序固定、无多余空格
        // ================================================================================

        /// <summary>
        /// 钉住**整个批次的确切字节**。一条断言同时覆盖五件事：
        /// 批头字段顺序（协议 §3.5 表格顺序）、字段齐全一个不缺、无缩进无多余空格、
        /// <c>tags</c> 的四个 kv 及其固定顺序，以及 <c>fields</c> 只剩 <c>stack</c>。
        /// <para>
        /// 这条挂了就说明批次格式变了 —— 而批次格式一变，签名就会失效，
        /// 现象是"所有请求 code=2"，没有别的线索。所以宁可让它挂在这里。
        /// </para>
        /// </summary>
        [Test]
        public void Header_FieldOrderAndCompleteness_IsExact()
        {
            string text = OneText(Rec("connect gateway", "at Foo()"), FullCtx());

            Assert.AreEqual(
                @"{""v"":1,""app_id"":""game_a"",""client_id"":""cid-1"",""uid"":""10086"","
                + @"""channel"":""taptap"",""app_version"":""1.2.3"",""res_version"":""1.2.5"","
                + @"""platform"":""android"",""device"":""Pixel 8"",""topic"":""client"","
                + @"""tags"":{""env"":""prod"",""gpu"":""Adreno"",""system"":""Android 14"","
                + @"""trace_id"":""trace-1""},""logs"":[{""ts"":1756881000123,""level"":""Log"","
                + @"""msg"":""connect gateway"",""fields"":{""stack"":""at Foo()""}}]}",
                text);
        }

        /// <summary>批头里协议 §3.5 列出的每一个 key 都必须存在（逐个点名，防"顺手删一个"）。</summary>
        [Test]
        public void Header_EveryProtocolKeyPresent()
        {
            string text = OneText(Rec("x"), FullCtx());
            JObject root = JsonConvert.DeserializeObject<JObject>(text);

            string[] keys =
            {
                "v", "app_id", "client_id", "uid", "channel", "app_version",
                "res_version", "platform", "device", "topic", "tags", "logs",
            };

            foreach (string key in keys)
            {
                Assert.IsTrue(root.ContainsKey(key), "批头缺字段: " + key);
            }

            Assert.AreEqual(keys.Length, root.Count, "批头多了协议之外的字段");
            Assert.AreEqual(1, (int)root["v"], "v 固定 1");
            Assert.AreEqual("client", (string)root["topic"], "topic 固定 client");
            Assert.AreEqual(AppId, (string)root["app_id"], "app_id 必须与 X-App-Id 一致");

            // tags 的 key 也逐个点名 —— 它现在承载着三个从 fields 上提的批次级维度，
            // 少一个就等于 CLS 里少一个检索维度，而这是静默的。
            JObject tags = (JObject)root["tags"];
            foreach (string key in TagKeys)
            {
                Assert.IsTrue(tags.ContainsKey(key), "tags 缺 kv: " + key);
            }

            Assert.AreEqual(TagKeys.Length, tags.Count, "tags 多了约定之外的 kv");
        }

        /// <summary>
        /// <c>tags</c> 的 kv **顺序**（0000-index「批次字段归属」补记）。
        /// 顺序由 LogBatch 里那几行的行序固定，不经过任何字典/哈希。
        /// </summary>
        private static readonly string[] TagKeys = { "env", "gpu", "system", "trace_id" };

        /// <summary>
        /// **未登录场景**：uid 是空串**且 key 存在**。
        /// <para>
        /// ⚠️ 用字符串包含断言。只看反序列化后的对象区分不出「空串」和「缺失」——
        /// 两者都会得到 null / ""，而协议明确要求字段必须存在。
        /// </para>
        /// </summary>
        [Test]
        public void Header_UnknownRequiredFields_AreEmptyStringNotMissing()
        {
            string text = OneText(Rec("boot"), Ctx(0));

            StringAssert.Contains(@"""uid"":""""", text);
            StringAssert.Contains(@"""res_version"":""""", text);
            StringAssert.Contains(@"""client_id"":""""", text);
            StringAssert.Contains(@"""app_version"":""""", text);
            StringAssert.Contains(@"""platform"":""""", text);
            StringAssert.Contains(EmptyTagsJson, text);

            Assert.IsFalse(text.Contains("null"), "必填字段拿不到值时传空串，绝不传 null");
        }

        /// <summary>启动早期什么都没就绪时 tags 的确切形态：四个 key 都在，值都是空串。</summary>
        private const string EmptyTagsJson =
            @"""tags"":{""env"":"""",""gpu"":"""",""system"":"""",""trace_id"":""""}";

        /// <summary>
        /// <c>tags</c> 装的是**批次内不变量**：env / gpu / system / trace_id，
        /// **恰好 4 个 kv**（0000-index「批次字段归属」补记）。
        /// <para>
        /// ⚠️ **协议上限 5 个，超 5 个整批返回 <c>code=1</c>** —— 那是整批被拒、
        /// 只能靠 TTL 淘汰，不是掉几条日志。所以这里把"不超 5"单独钉死一条，
        /// 而不是只断言"等于 4"：等于 4 说的是今天的取值，不超 5 说的是不许越界。
        /// </para>
        /// </summary>
        [Test]
        public void Header_Tags_AreFourBatchInvariants_AndWithinProtocolLimit()
        {
            string text = OneText(Rec("x"), FullCtx());
            JObject tags = (JObject)JsonConvert.DeserializeObject<JObject>(text)["tags"];

            Assert.AreEqual(4, tags.Count, "tags = env / gpu / system / trace_id");
            Assert.LessOrEqual(tags.Count, 5, "超 5 个 kv 会让整批 code=1 被拒");

            Assert.AreEqual("prod", (string)tags["env"]);
            Assert.AreEqual("Adreno", (string)tags["gpu"]);
            Assert.AreEqual("Android 14", (string)tags["system"]);
            Assert.AreEqual("trace-1", (string)tags["trace_id"]);

            foreach (var pair in tags)
            {
                Assert.AreEqual(JTokenType.String, pair.Value.Type,
                    "tags." + pair.Key + " 必须是字符串（协议：值统一用字符串）");
            }
        }

        /// <summary>
        /// <c>tags</c> 的 kv **顺序确定**：同一输入两次序列化，字节完全相同，
        /// 且 key 的出场次序就是 env → gpu → system → trace_id。
        /// <para>
        /// 为什么要单独钉：tags 是这个批次里唯一一处"一组 kv"，最容易被后来人
        /// 改成 <c>Dictionary</c> 一把写出去 —— 那样顺序就取决于哈希，字节不再确定，
        /// 签名随之失效，现象是"所有请求 code=2"。
        /// </para>
        /// </summary>
        [Test]
        public void Header_Tags_KeyOrder_IsFixedAndDeterministic()
        {
            LogContextSnapshot ctx = FullCtx();

            byte[] first = One(Rec("x"), ctx);
            byte[] second = One(Rec("x"), ctx);
            CollectionAssert.AreEqual(first, second, "同一输入必须产出逐字节相同的 tags");

            string text = Text(first);
            int at = text.IndexOf(@"""tags"":{", StringComparison.Ordinal);
            Assert.Greater(at, 0, "tags 必须存在");

            int previous = at;
            foreach (string key in TagKeys)
            {
                int here = text.IndexOf(@"""" + key + @""":", at, StringComparison.Ordinal);
                Assert.Greater(here, previous, "tags 的 kv 顺序必须是 env → gpu → system → trace_id");
                previous = here;
            }
        }

        /// <summary>
        /// 宿主没传 topic 时回落到 "client"，**不发空串** ——
        /// 空 topic 会让整批 code=1，那是"配置漏了"而不是"该发空值"。
        /// </summary>
        [Test]
        public void Header_EmptyTopic_FallsBackToClient()
        {
            List<LogBatchPayload> batches = LogBatch.Build(
                new List<LogRecord> { Rec("x") }, Fixed(FullCtx()), AppId, "", null);

            StringAssert.Contains(@"""topic"":""client""", Text(batches[0].Bytes));
        }

        // ================================================================================
        //  2. 条目 —— ts 单位、level 原值、fields 全字符串
        // ================================================================================

        /// <summary>
        /// ⚠️ **单位陷阱**：logs[].ts 是**毫秒**（13 位），X-Timestamp 是**秒**（10 位）。
        /// 协议 §6 的检查清单专门列了这条，说明服务端已经被坑过。
        /// </summary>
        [Test]
        public void Entry_Ts_IsThirteenDigitMilliseconds()
        {
            string text = OneText(Rec("x", ts: 1756881000123L), FullCtx());

            StringAssert.Contains(@"""ts"":1756881000123", text);

            JObject root = JsonConvert.DeserializeObject<JObject>(text);
            long ts = (long)root["logs"][0]["ts"];
            Assert.AreEqual(13, ts.ToString(CultureInfo.InvariantCulture).Length,
                "ts 必须是 13 位毫秒，不是 10 位秒");
            Assert.AreEqual(1756881000123L, ts, "ts 用 TsUnixMs 原始值，不做任何时钟校正");
        }

        /// <summary>
        /// ts 原样透传，**绝不做时钟校正**（全局硬约束 5 / ADR-0002 末段）：
        /// 一个明显偏离的设备时间必须原样上报，否则服务端就再也识别不出时钟异常设备。
        /// </summary>
        [Test]
        public void Entry_Ts_PassesThroughEvenWhenDeviceClockIsWrong()
        {
            const long skewed = 1000000000000L;
            string text = OneText(Rec("x", ts: skewed), FullCtx());

            StringAssert.Contains(@"""ts"":1000000000000", text);
        }

        /// <summary>level 用 UnityEngine.LogType 原值字符串，5 种，不做映射。</summary>
        [Test]
        public void Entry_Level_UsesRawLogTypeNames()
        {
            var expected = new Dictionary<LogType, string>
            {
                { LogType.Error, "Error" },
                { LogType.Assert, "Assert" },
                { LogType.Warning, "Warning" },
                { LogType.Log, "Log" },
                { LogType.Exception, "Exception" },
            };

            foreach (var pair in expected)
            {
                string text = OneText(Rec("x", type: pair.Key), FullCtx());
                StringAssert.Contains(@"""level"":""" + pair.Value + @"""", text);
            }

            Assert.AreEqual(5, expected.Count, "LogType 就是 5 种");
        }

        /// <summary>
        /// <c>fields</c> **只剩 stack 一个 key**，值是字符串 ——
        /// 不同客户端序列化出不同类型会让 CLS 侧索引冲突。
        /// <para>
        /// gpu / system / trace_id 已经上提到批次级 tags（0000-index「批次字段归属」补记）。
        /// </para>
        /// </summary>
        [Test]
        public void Entry_Fields_HasOnlyStack_AsString()
        {
            string text = OneText(Rec("x", "at Foo()"), FullCtx());
            JObject fields = (JObject)JsonConvert.DeserializeObject<JObject>(text)["logs"][0]["fields"];

            Assert.AreEqual(1, fields.Count, "fields 只放 stack");
            Assert.AreEqual(JTokenType.String, fields["stack"].Type, "fields.stack 必须是字符串");
            Assert.AreEqual("at Foo()", (string)fields["stack"]);
        }

        /// <summary>
        /// <c>fields</c> **总是存在、值可能是空串** —— stack 为空时也照写，不省略。
        /// <para>
        /// 简单确定优先：批次格式可预期，空值 kv 服务端本来就会丢弃。
        /// 这条防的是"顺手优化成 stack 为空就不写 fields"。
        /// </para>
        /// </summary>
        [Test]
        public void Entry_Fields_AlwaysPresentEvenWhenStackIsEmpty()
        {
            string text = OneText(Rec("no stack here", ""), FullCtx());

            StringAssert.Contains(@"""fields"":{""stack"":""""}", text);
        }

        /// <summary>
        /// ⚠️ **批次内不变量绝不逐条重复**：gpu / system / trace_id 在整个 payload 里
        /// 各只出现**一次**（在批次级 tags 里），哪怕批次有很多条日志。
        /// <para>
        /// 这条是本次改动的全部收益所在 —— 真机取证显示逐条重复这三个值占了 outbox
        /// 原始字节的 10%（一个批次里 system 重复 1271 次）。断言打在**整串 payload
        /// 的文本**上而不是解析后的对象上：对象看不见"重复了多少遍"。
        /// </para>
        /// </summary>
        [Test]
        public void Entry_Fields_NeverRepeatBatchInvariants()
        {
            List<LogRecord> records = new List<LogRecord>();
            for (int i = 0; i < 50; i++)
            {
                records.Add(Rec("line " + i, "at Foo()"));
            }

            List<LogBatchPayload> batches = LogBatch.Build(
                records, Fixed(FullCtx()), AppId, Topic, new LogConfig());

            Assert.AreEqual(1, batches.Count, "夹具本身应当只产出一个批次");
            string text = Text(batches[0].Bytes);
            Assert.AreEqual(50, CountOf(text, @"""stack"":"), "stack 才是逐条的那个");

            foreach (string key in new[] { "gpu", "system", "trace_id" })
            {
                Assert.AreEqual(1, CountOf(text, @"""" + key + @""":"),
                    "key \"" + key + "\" 必须只出现在批次级 tags 里，一次");
            }

            // 连**值**也只写一遍 —— key 没了但值被抄 50 遍的话收益就是零。
            Assert.AreEqual(1, CountOf(text, "Adreno"), "gpu 的值只写一遍");
            Assert.AreEqual(1, CountOf(text, "Android 14"), "system 的值只写一遍");
            Assert.AreEqual(1, CountOf(text, "trace-1"), "trace_id 的值只写一遍");
        }

        /// <summary>子串出现次数（重叠不计）。只在测试代码里用。</summary>
        private static int CountOf(string haystack, string needle)
        {
            int count = 0;
            int at = 0;

            while (true)
            {
                int here = haystack.IndexOf(needle, at, StringComparison.Ordinal);
                if (here < 0)
                {
                    return count;
                }

                count++;
                at = here + needle.Length;
            }
        }

        // ================================================================================
        //  3. 转义
        // ================================================================================

        /// <summary>双引号与反斜杠 —— 最容易把 JSON 写崩的两个字符。</summary>
        [Test]
        public void Escape_QuoteAndBackslash()
        {
            const string raw = "say \"hi\" and C:\\path\\to";
            string text = OneText(Rec(raw), FullCtx());

            StringAssert.Contains(@"\""hi\""", text);
            StringAssert.Contains(@"C:\\path\\to", text);
            Assert.AreEqual(raw, MsgOf(text), "反向解析必须还原成原文");
        }

        /// <summary>\n \r \t 走短转义。</summary>
        [Test]
        public void Escape_NewlineCarriageReturnTab()
        {
            const string raw = "a\nb\rc\td";
            string text = OneText(Rec(raw), FullCtx());

            StringAssert.Contains(@"a\nb\rc\td", text);
            Assert.IsFalse(text.Contains("\n"), "产出必须是单行，不能有真的换行");
            Assert.IsFalse(text.Contains("\r"), "产出必须是单行，不能有真的回车");
            Assert.IsFalse(text.Contains("\t"), "产出里不能有真的制表符");
            Assert.AreEqual(raw, MsgOf(text));
        }

        /// <summary>
        /// 其余控制字符 U+0000~U+001F 一律转成 \uXXXX。
        /// <para>这一段是最容易漏的：它们在编辑器里看不见，但会让 JSON 直接非法。</para>
        /// </summary>
        [Test]
        public void Escape_AllControlCharacters()
        {
            StringBuilder sb = new StringBuilder();
            for (int c = 0x00; c <= 0x1F; c++)
            {
                sb.Append((char)c);
            }

            string raw = sb.ToString();
            string text = OneText(Rec(raw), FullCtx());

            StringAssert.Contains(@"\u0000", text);
            StringAssert.Contains(@"\u0001", text);
            Assert.GreaterOrEqual(
                text.IndexOf(@"\u001f", StringComparison.OrdinalIgnoreCase), 0,
                "U+001F 必须被转义");

            for (int c = 0x00; c <= 0x1F; c++)
            {
                Assert.IsFalse(text.IndexOf((char)c) >= 0,
                    "裸控制字符 U+" + c.ToString("X4") + " 不允许出现在产出里");
            }

            Assert.AreEqual(raw, MsgOf(text), "反向解析必须逐字还原全部控制字符");
        }

        /// <summary>
        /// 中文**直接输出 UTF-8 原文，不转 \uXXXX**。
        /// <para>
        /// 转 \uXXXX 是 StringEscapeHandling.EscapeNonAscii 这个可选值才有的行为，
        /// 不是默认值 —— 这条测试就是钉住我们没有把它打开（转义了体积会涨一倍多）。
        /// </para>
        /// </summary>
        [Test]
        public void Escape_Chinese_StaysRawUtf8()
        {
            const string raw = "登录失败：网络超时";
            byte[] payload = One(Rec(raw), FullCtx());
            string text = Text(payload);

            StringAssert.Contains(raw, text);
            Assert.IsFalse(text.Contains(@"\u767b"), "非 ASCII 不得转义成 \\uXXXX");
            AssertContainsBytes(Encoding.UTF8.GetBytes(raw), payload,
                "中文必须以 UTF-8 原文（连续字节）出现在 payload 里");
            Assert.AreEqual(raw, MsgOf(text));
        }

        /// <summary>emoji 是**代理对**：一个 code point 占两个 char、UTF-8 里占 4 字节。</summary>
        [Test]
        public void Escape_Emoji_SurrogatePairSurvives()
        {
            const string raw = "boom 😀 done";
            byte[] payload = One(Rec(raw), FullCtx());
            string text = Text(payload);

            StringAssert.Contains(raw, text);
            AssertContainsBytes(new byte[] { 0xF0, 0x9F, 0x98, 0x80 }, payload,
                "emoji 必须是完整且连续的 4 字节 UTF-8 序列");
            Assert.IsFalse(text.Contains("\uFFFD"), "不得出现替换字符 —— 那是代理对被劈开的症状");
            Assert.AreEqual(raw, MsgOf(text));
        }

        // ================================================================================
        //  4. 确定性
        // ================================================================================

        /// <summary>同一输入两次调用，产出的字节**完全相同**。签名的前提就是这个。</summary>
        [Test]
        public void Determinism_SameInputProducesIdenticalBytes()
        {
            LogContextSnapshot ctx = FullCtx();
            LogRecord record = Rec("中文 \"quote\" 😀\n第二行", "at Foo()\nat Bar()");

            byte[] first = One(record, ctx);
            byte[] second = One(record, ctx);

            CollectionAssert.AreEqual(first, second);
        }

        /// <summary>产出的字节能被标准 JSON 解析器解析（Newtonsoft 反向验证，仅测试代码里用）。</summary>
        [Test]
        public void Determinism_OutputIsParsableJson()
        {
            StringBuilder sb = new StringBuilder();
            for (int c = 0x00; c <= 0x1F; c++)
            {
                sb.Append((char)c);
            }

            sb.Append("中文 😀 \"q\" \\ end");

            string text = OneText(Rec(sb.ToString(), "at Foo()\r\nat Bar()"), FullCtx());

            JObject root = null;
            Assert.DoesNotThrow(() => root = JsonConvert.DeserializeObject<JObject>(text));
            Assert.IsNotNull(root);
            Assert.AreEqual(1, ((JArray)root["logs"]).Count);
        }

        /// <summary>产出无 BOM —— BOM 会进签名载荷，让服务端算出另一个 sha256。</summary>
        [Test]
        public void Determinism_NoBomAndSingleLine()
        {
            byte[] payload = One(Rec("x"), FullCtx());

            Assert.AreEqual((byte)'{', payload[0], "第一个字节必须是 '{'，不能有 BOM");
            Assert.AreEqual((byte)'}', payload[payload.Length - 1]);
            Assert.IsFalse(Text(payload).Contains(" \""), "不得出现多余空格");
        }

        // ================================================================================
        //  5. 与 02 号票的联动：payload_raw → LogSigner.Sha256Hex
        // ================================================================================

        /// <summary>
        /// 把本票产出的 payload_raw 直接喂给 02 号票的 <see cref="LogSigner.Sha256Hex"/>，
        /// 得到稳定 hash —— 证明两票的接口对得上，且字节没有在中间被任何人改动过。
        /// <para>
        /// 除了"两次一致"，还比对了"对同一段期望文本求出的 hash"，
        /// 这样连"产出的字节到底是不是那串文本"也一并钉死了。
        /// </para>
        /// </summary>
        [Test]
        public void Signer_Sha256OfPayloadRaw_IsStable()
        {
            byte[] payload = One(Rec("connect gateway", "at Foo()"), FullCtx());

            string first = LogSigner.Sha256Hex(payload);
            string second = LogSigner.Sha256Hex(One(Rec("connect gateway", "at Foo()"), FullCtx()));

            Assert.AreEqual(64, first.Length, "sha256 是 64 个十六进制字符");
            Assert.AreEqual(first, second, "同一批次两次序列化必须得到同一个摘要");

            string expected = LogSigner.Sha256Hex(Encoding.UTF8.GetBytes(Text(payload)));
            Assert.AreEqual(expected, first, "payload_raw 就是那串文本的 UTF-8 字节，中途没被改过");
        }

        // ================================================================================
        //  6. 截断 —— msg
        // ================================================================================

        private static LogConfig SmallLimits(int msgMax = 100, int stackMax = 100)
        {
            LogConfig cfg = new LogConfig();
            cfg.MsgMaxBytes = msgMax;
            cfg.StackMaxBytes = stackMax;
            return cfg;
        }

        /// <summary>恰好等于上限：**不截断**，一个字节都不动。</summary>
        [Test]
        public void Truncate_MsgExactlyAtLimit_IsUntouched()
        {
            LogConfig cfg = SmallLimits();
            string raw = Repeat('a', cfg.MsgMaxBytes);

            string text = OneText(Rec(raw), FullCtx(), cfg);

            Assert.AreEqual(raw, MsgOf(text));
            Assert.IsFalse(text.Contains("truncated"));
        }

        /// <summary>超一字节：截断，且**截断标记算在上限内**，总长度不超限。</summary>
        [Test]
        public void Truncate_MsgOverLimitByOneByte_IsTruncatedWithinLimit()
        {
            LogConfig cfg = SmallLimits();
            string raw = Repeat('a', cfg.MsgMaxBytes + 1);

            string msg = MsgOf(OneText(Rec(raw), FullCtx(), cfg));

            StringAssert.EndsWith(" bytes]", msg);
            StringAssert.StartsWith("aaa", msg);
            Assert.LessOrEqual(Utf8Len(msg), cfg.MsgMaxBytes,
                "截到上限再追加标记会重新超限 —— 正确做法是截到「上限 − 标记长度」");
        }

        /// <summary>远超上限：同样不超限，且标记里报的丢弃字节数是真实值。</summary>
        [Test]
        public void Truncate_MsgFarOverLimit_ReportsRealDroppedBytes()
        {
            LogConfig cfg = SmallLimits();
            string raw = Repeat('a', 10000);

            string msg = MsgOf(OneText(Rec(raw), FullCtx(), cfg));

            Assert.LessOrEqual(Utf8Len(msg), cfg.MsgMaxBytes);

            int kept = msg.IndexOf("...[truncated ", StringComparison.Ordinal);
            Assert.Greater(kept, 0, "保头：前面必须留下正文");

            string marker = msg.Substring(kept);
            int dropped = int.Parse(
                marker.Replace("...[truncated ", "").Replace(" bytes]", ""),
                CultureInfo.InvariantCulture);
            Assert.AreEqual(10000 - kept, dropped, "标记里的 N 必须是真实丢弃的字节数");
        }

        /// <summary>
        /// 多字节字符（中文 3 字节）正好落在边界上时**不产生半个字符**。
        /// <para>劈开的症状是编码器吐出 U+FFFD，正文从那一刻起就不再是原文。</para>
        /// </summary>
        [Test]
        public void Truncate_MultiByteCharAtBoundary_IsNotSplit()
        {
            LogConfig cfg = SmallLimits();
            string raw = Repeat('中', 50); // 150 字节，边界必然落在某个 3 字节字符中间

            string text = OneText(Rec(raw), FullCtx(), cfg);
            string msg = MsgOf(text);

            Assert.LessOrEqual(Utf8Len(msg), cfg.MsgMaxBytes);
            Assert.IsFalse(msg.Contains("\uFFFD"), "不得把 3 字节字符劈开");

            string kept = msg.Substring(0, msg.IndexOf("...[truncated ", StringComparison.Ordinal));
            Assert.AreEqual(0, Utf8Len(kept) % 3, "保留段必须是整数个中文字符");
            StringAssert.StartsWith(kept, raw, "保头：保留段必须是原文的前缀");
        }

        /// <summary>
        /// 代理对（emoji，UTF-8 里 4 字节）落在边界上时不被劈开。
        /// <para>劈开会留下一个孤立的高位代理，UTF-8 编码时变成 U+FFFD。</para>
        /// </summary>
        [Test]
        public void Truncate_SurrogatePairAtBoundary_IsNotSplit()
        {
            LogConfig cfg = SmallLimits(msgMax: 30);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 10; i++)
            {
                sb.Append("😀");
            }

            string text = OneText(Rec(sb.ToString()), FullCtx(), cfg);
            string msg = MsgOf(text);

            Assert.LessOrEqual(Utf8Len(msg), cfg.MsgMaxBytes);
            Assert.IsFalse(msg.Contains("\uFFFD"), "不得把代理对劈开");

            string kept = msg.Substring(0, msg.IndexOf("...[truncated ", StringComparison.Ordinal));
            Assert.AreEqual(0, kept.Length % 2, "保留段必须是整数个代理对");
            for (int i = 0; i < kept.Length; i += 2)
            {
                Assert.IsTrue(char.IsHighSurrogate(kept[i]), "高位代理");
                Assert.IsTrue(char.IsLowSurrogate(kept[i + 1]), "低位代理必须跟着高位一起保留");
            }

            Assert.AreEqual(msg, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(msg)),
                "UTF-8 往返必须无损 —— 有半个字符就会在这里露馅");
        }

        /// <summary>截断后仍是合法 JSON（标记本身不含需要转义的字符，但还是要验一遍）。</summary>
        [Test]
        public void Truncate_ResultIsStillValidJson()
        {
            LogConfig cfg = SmallLimits();
            string text = OneText(Rec(Repeat('中', 200), Repeat('x', 500)), FullCtx(), cfg);

            Assert.DoesNotThrow(() => JsonConvert.DeserializeObject<JObject>(text));
        }

        // ================================================================================
        //  7. 截断 —— stack 独立
        // ================================================================================

        /// <summary>
        /// fields.stack 用 <see cref="LogConfig.StackMaxBytes"/> **独立截断**，
        /// 不影响同一条的 msg。
        /// <para>
        /// 为什么要单独截：协议只规定了 msg 上限，但一个递归爆栈的堆栈能有几 MB，
        /// 不单独截的话单条就能撑破整批上限 → 整批 code=1 被拒 →
        /// 只能等 TTL 淘汰，在此之前一直重试白耗预算。
        /// </para>
        /// </summary>
        [Test]
        public void Truncate_Stack_IsIndependentFromMsg()
        {
            LogConfig cfg = SmallLimits(msgMax: 200, stackMax: 60);
            string msgRaw = Repeat('m', 150);   // < msgMax，不该被截
            string stackRaw = Repeat('s', 5000); // >> stackMax，必须被截

            string text = OneText(Rec(msgRaw, stackRaw), FullCtx(), cfg);

            Assert.AreEqual(msgRaw, MsgOf(text), "msg 没超自己的上限，不许被 stack 连累");

            string stack = StackOf(text);
            StringAssert.EndsWith(" bytes]", stack);
            Assert.LessOrEqual(Utf8Len(stack), cfg.StackMaxBytes);
        }

        /// <summary>反过来也成立：msg 超限被截时，没超限的 stack 原样保留。</summary>
        [Test]
        public void Truncate_Msg_DoesNotAffectShortStack()
        {
            LogConfig cfg = SmallLimits(msgMax: 60, stackMax: 200);
            string stackRaw = Repeat('s', 100);

            string text = OneText(Rec(Repeat('m', 5000), stackRaw), FullCtx(), cfg);

            Assert.AreEqual(stackRaw, StackOf(text));
            StringAssert.EndsWith(" bytes]", MsgOf(text));
        }

        /// <summary>爆栈级别的堆栈（几 MB）撑不破一个批次 —— 这就是 stack 独立截断的目的。</summary>
        [Test]
        public void Truncate_ExplodedStack_CannotBlowUpTheBatch()
        {
            LogConfig cfg = new LogConfig();
            string stackRaw = Repeat('s', 3 * 1024 * 1024);

            byte[] payload = One(Rec("stack overflow", stackRaw), FullCtx(), cfg);

            Assert.Less(payload.Length, cfg.BatchMaxBytes,
                "单条爆栈不得撑破 BatchMaxBytes");
            Assert.LessOrEqual(Utf8Len(StackOf(Text(payload))), cfg.StackMaxBytes);
        }

        // ================================================================================
        //  8. 切批
        // ================================================================================

        /// <summary>一次交换 500 条时按 BatchMaxCount 正确分组。</summary>
        [Test]
        public void Split_ByMaxCount()
        {
            LogConfig cfg = new LogConfig();
            List<LogRecord> records = new List<LogRecord>();
            for (int i = 0; i < 500; i++)
            {
                records.Add(Rec("line " + i));
            }

            List<LogBatchPayload> batches = LogBatch.Build(
                records, Fixed(FullCtx()), AppId, Topic, cfg);

            Assert.AreEqual(5, batches.Count);
            for (int i = 0; i < batches.Count; i++)
            {
                Assert.AreEqual(cfg.BatchMaxCount, batches[i].Count, "第 " + i + " 批条数");
                Assert.AreEqual(i * cfg.BatchMaxCount, batches[i].Offset, "第 " + i + " 批起点");
            }
        }

        /// <summary>按最终序列化字节分组：每一批都不超 BatchMaxBytes，且没有记录被吞掉。</summary>
        [Test]
        public void Split_ByFinalSerializedBytes()
        {
            LogConfig cfg = new LogConfig();
            cfg.BatchMaxBytes = 4 * 1024;

            List<LogRecord> records = new List<LogRecord>();
            for (int i = 0; i < 200; i++)
            {
                records.Add(Rec(Repeat('a', 300)));
            }

            List<LogBatchPayload> batches = LogBatch.Build(
                records, Fixed(FullCtx()), AppId, Topic, cfg);

            Assert.Greater(batches.Count, 1, "300 字节 × 200 条必须切成多批");

            int total = 0;
            foreach (LogBatchPayload batch in batches)
            {
                Assert.LessOrEqual(batch.Bytes.Length, cfg.BatchMaxBytes,
                    "预算按最终序列化字节控制，任何一批都不得超");
                Assert.AreEqual(total, batch.Offset, "批次必须首尾相接，不能漏记录");
                total += batch.Count;
            }

            Assert.AreEqual(records.Count, total, "所有记录都要进批次");
        }

        /// <summary>
        /// **预算按最终序列化字节算，不是 LogRecord.Size**。
        /// <para>
        /// 这条是专门设的陷阱：正文全是双引号，转义后字节数**翻倍**，
        /// 而 <see cref="LogRecord.Size"/> 被填成 1（谎报）。
        /// 谁要是拿 Size 当预算，就会把全部 40 条塞进一个批次；
        /// 按最终字节算则必然切成多批，且每批都不超限。
        /// </para>
        /// </summary>
        [Test]
        public void Split_BudgetUsesFinalBytes_NotRecordSize()
        {
            LogConfig cfg = new LogConfig();
            cfg.BatchMaxBytes = 2 * 1024;

            List<LogRecord> records = new List<LogRecord>();
            for (int i = 0; i < 40; i++)
            {
                records.Add(Rec(Repeat('"', 200), size: 1));
            }

            List<LogBatchPayload> batches = LogBatch.Build(
                records, Fixed(FullCtx()), AppId, Topic, cfg);

            Assert.Greater(batches.Count, 1,
                "按 Size(=1) 算会全塞进一批 —— 必须按转义之后的最终字节算");

            foreach (LogBatchPayload batch in batches)
            {
                Assert.LessOrEqual(batch.Bytes.Length, cfg.BatchMaxBytes);
            }
        }

        /// <summary>
        /// ⚠️ **CtxVer 变化处必须切批**（契约 C2）。
        /// <para>
        /// 这是本模块存在的全部意义：一条登录前产生的日志若和登录后的日志同批，
        /// 就会被打上登录后的 uid，凭空归到某个玩家名下 —— 查不出来也洗不掉。
        /// </para>
        /// </summary>
        [Test]
        public void Split_OnCtxVerChange_PreLoginLogsKeepEmptyUid()
        {
            LogContextSnapshot before = Ctx(1, uid: "", env: "", clientId: "cid-1");
            LogContextSnapshot after = Ctx(2, uid: "10086", env: "prod", clientId: "cid-1");

            List<LogRecord> records = new List<LogRecord>
            {
                Rec("boot", ctxVer: 1),
                Rec("sdk init", ctxVer: 1),
                Rec("login ok", ctxVer: 2),
                Rec("enter world", ctxVer: 2),
            };

            List<LogBatchPayload> batches = LogBatch.Build(
                records,
                ver => ver == 1 ? before : after,
                AppId, Topic, new LogConfig());

            Assert.AreEqual(2, batches.Count, "CtxVer 变化处必须切批");

            Assert.AreEqual(1, batches[0].CtxVer);
            Assert.AreEqual(2, batches[0].Count);
            StringAssert.Contains(@"""uid"":""""", Text(batches[0].Bytes));
            Assert.IsFalse(Text(batches[0].Bytes).Contains("10086"),
                "登录前的日志绝不能带上登录后的 uid");

            Assert.AreEqual(2, batches[1].CtxVer);
            Assert.AreEqual(2, batches[1].Count);
            StringAssert.Contains(@"""uid"":""10086""", Text(batches[1].Bytes));
        }

        /// <summary>CtxVer 切批优先于条数/字节：哪怕只有一条也要单独成批。</summary>
        [Test]
        public void Split_CtxVerChange_BeatsCountAndBytes()
        {
            List<LogRecord> records = new List<LogRecord>
            {
                Rec("a", ctxVer: 1),
                Rec("b", ctxVer: 2),
                Rec("c", ctxVer: 3),
            };

            List<LogBatchPayload> batches = LogBatch.Build(
                records, ver => Ctx(ver, uid: "u" + ver), AppId, Topic, new LogConfig());

            Assert.AreEqual(3, batches.Count);
            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual(i + 1, batches[i].CtxVer);
                Assert.AreEqual(1, batches[i].Count);
                StringAssert.Contains(@"""uid"":""u" + (i + 1) + @"""", Text(batches[i].Bytes));
            }
        }

        /// <summary>批头用的是**该批 CtxVer 对应的快照**，不是"当前最新的快照"。</summary>
        [Test]
        public void Split_HeaderUsesBatchOwnSnapshot_NotLatest()
        {
            LogContextSnapshot v1 = Ctx(1, resVersion: "", env: "");
            LogContextSnapshot v2 = Ctx(2, resVersion: "9.9.9", env: "prod");

            List<LogRecord> records = new List<LogRecord>
            {
                Rec("before hotfix", ctxVer: 1),
                Rec("after hotfix", ctxVer: 2),
            };

            List<LogBatchPayload> batches = LogBatch.Build(
                records, ver => ver == 1 ? v1 : v2, AppId, Topic, new LogConfig());

            StringAssert.Contains(@"""res_version"":""""", Text(batches[0].Bytes));
            StringAssert.Contains(EmptyTagsJson, Text(batches[0].Bytes));
            StringAssert.Contains(@"""res_version"":""9.9.9""", Text(batches[1].Bytes));
            StringAssert.Contains(@"""tags"":{""env"":""prod""", Text(batches[1].Bytes));
        }

        /// <summary>
        /// 单条自己就超过 BatchMaxBytes 时**照发不误**（独占一批），不丢弃、不死循环。
        /// <para>丢弃等于让最该看的那条日志消失。</para>
        /// </summary>
        [Test]
        public void Split_OversizedSingleRecord_StillEmitted()
        {
            LogConfig cfg = new LogConfig();
            cfg.BatchMaxBytes = 64; // 比空批头还小

            List<LogRecord> records = new List<LogRecord>
            {
                Rec("aaa"), Rec("bbb"), Rec("ccc"),
            };

            List<LogBatchPayload> batches = LogBatch.Build(
                records, Fixed(FullCtx()), AppId, Topic, cfg);

            Assert.AreEqual(3, batches.Count, "装不下也要一条一批地发出去");
            for (int i = 0; i < 3; i++)
            {
                Assert.AreEqual(1, batches[i].Count);
            }
        }

        /// <summary>敏感记录不进批次（全局硬约束 7），且不会因此产出空 logs 的批次。</summary>
        [Test]
        public void Split_SensitiveRecords_NeverEnterPayload()
        {
            List<LogRecord> records = new List<LogRecord>
            {
                Rec("token=abcdef", sensitive: true),
                Rec("normal line"),
                Rec("secret=xyz", sensitive: true),
            };

            List<LogBatchPayload> batches = LogBatch.Build(
                records, Fixed(FullCtx()), AppId, Topic, new LogConfig());

            Assert.AreEqual(1, batches.Count);
            Assert.AreEqual(1, batches[0].Count);

            string text = Text(batches[0].Bytes);
            Assert.IsFalse(text.Contains("token=abcdef"), "敏感正文绝不能进 payload");
            Assert.IsFalse(text.Contains("secret=xyz"));
            StringAssert.Contains("normal line", text);
        }

        /// <summary>整段都是敏感记录时不产出任何批次 —— 空 logs 会让整批 code=1。</summary>
        [Test]
        public void Split_AllSensitive_ProducesNoBatch()
        {
            List<LogRecord> records = new List<LogRecord>
            {
                Rec("token=1", sensitive: true),
                Rec("token=2", sensitive: true),
            };

            List<LogBatchPayload> batches = LogBatch.Build(
                records, Fixed(FullCtx()), AppId, Topic, new LogConfig());

            Assert.AreEqual(0, batches.Count);
        }

        /// <summary>Internal 诊断日志不影响切批，正常混在批次里（全局硬约束 6）。</summary>
        [Test]
        public void Split_InternalRecords_AreBatchedNormally()
        {
            List<LogRecord> records = new List<LogRecord>
            {
                Rec("business"),
                Rec("[log] dropped 3 records", isInternal: true),
            };

            List<LogBatchPayload> batches = LogBatch.Build(
                records, Fixed(FullCtx()), AppId, Topic, new LogConfig());

            Assert.AreEqual(1, batches.Count);
            Assert.AreEqual(2, batches[0].Count);
            StringAssert.Contains("dropped 3 records", Text(batches[0].Bytes));
        }

        // ================================================================================
        //  9. CtxVer → 快照的解析（配不上时的降级）
        // ================================================================================

        /// <summary>见过的版本能被原样取回。</summary>
        [Test]
        public void ContextCache_ResolvesObservedVersion()
        {
            LogBatchContextCache cache = new LogBatchContextCache();
            LogContextSnapshot v1 = Ctx(1, uid: "old");
            LogContextSnapshot v2 = Ctx(2, uid: "new");

            cache.Observe(v1);
            cache.Observe(v2);

            Assert.AreSame(v1, cache.Resolve(1, v2));
            Assert.AreSame(v2, cache.Resolve(2, v2));
        }

        /// <summary>
        /// 没见过的版本走降级：**设备级字段保留，会变的四个字段清空**。
        /// <para>最坏是少几个维度，绝不能出现"把登录前的日志打上登录后的 uid"。</para>
        /// </summary>
        [Test]
        public void ContextCache_UnknownVersion_DegradesInsteadOfMisattributing()
        {
            LogBatchContextCache cache = new LogBatchContextCache();
            LogContextSnapshot latest = FullCtx(9);

            LogContextSnapshot resolved = cache.Resolve(3, latest);

            Assert.AreEqual(3, resolved.Version);
            Assert.AreEqual("", resolved.Uid, "宁可没有 uid，也不能张冠李戴");
            Assert.AreEqual("", resolved.Env);
            Assert.AreEqual("", resolved.ResVersion);
            Assert.AreEqual("", resolved.Channel);

            Assert.AreEqual("1.2.3", resolved.AppVersion, "设备级字段在 Init 之后不再变，照常带上");
            Assert.AreEqual("android", resolved.Platform);
            Assert.AreEqual("Pixel 8", resolved.Device);
            Assert.AreEqual("cid-1", resolved.ClientId);
            Assert.AreEqual("trace-1", resolved.TraceId);
        }

        /// <summary>环满之后仍能取回最近的版本，且重复 Observe 不挤格子。</summary>
        [Test]
        public void ContextCache_RingKeepsRecentVersions()
        {
            LogBatchContextCache cache = new LogBatchContextCache();
            for (int v = 1; v <= LogBatchContextCache.Capacity; v++)
            {
                cache.Observe(Ctx(v, uid: "u" + v));
                cache.Observe(Ctx(v, uid: "u" + v)); // 重复喂不该占额外格子
            }

            LogContextSnapshot latest = Ctx(99);
            Assert.AreEqual("u1", cache.Resolve(1, latest).Uid);
            Assert.AreEqual("u" + LogBatchContextCache.Capacity,
                cache.Resolve(LogBatchContextCache.Capacity, latest).Uid);
        }

        // ================================================================================
        //  10. sink（契约 C4 的接线）
        // ================================================================================

        private sealed class FakeEnvironment : ILogBatchEnvironment
        {
            internal LogContextSnapshot Snapshot = LogContextSnapshot.Empty;
            internal LogConfig Cfg = new LogConfig();

            public string AppId
            {
                get { return LogBatchTests.AppId; }
            }

            public string Topic
            {
                get { return LogBatchTests.Topic; }
            }

            public LogContextSnapshot Context
            {
                get { return Snapshot; }
            }

            public LogConfig Config
            {
                get { return Cfg; }
            }
        }

        /// <summary>sink 把整段记录切完并逐批交给 handler —— 尾巴也交，不留在内存里。</summary>
        [Test]
        public void Sink_WriteEmitsEveryBatchIncludingTail()
        {
            FakeEnvironment env = new FakeEnvironment { Snapshot = FullCtx(7) };
            env.Cfg.BatchMaxCount = 10;

            List<LogBatchPayload> got = new List<LogBatchPayload>();
            LogBatchSink sink = new LogBatchSink(env, p => got.Add(p));

            List<LogRecord> records = new List<LogRecord>();
            for (int i = 0; i < 25; i++)
            {
                records.Add(Rec("line " + i));
            }

            sink.Write(records);

            Assert.AreEqual(3, got.Count, "10 + 10 + 5，尾巴那 5 条也必须交出去");
            Assert.AreEqual(5, got[2].Count);
        }

        /// <summary>
        /// sink 认得 CtxVer：上一轮见过的快照，下一轮仍能被正确取回。
        /// </summary>
        [Test]
        public void Sink_RemembersPreviousSnapshotAcrossWrites()
        {
            FakeEnvironment env = new FakeEnvironment { Snapshot = Ctx(1, uid: "", clientId: "cid") };
            List<LogBatchPayload> got = new List<LogBatchPayload>();
            LogBatchSink sink = new LogBatchSink(env, p => got.Add(p));

            sink.Write(new List<LogRecord> { Rec("before login", ctxVer: 1) });

            // 登录发生：快照换成 v2，但缓冲里还压着一条 v1 的记录。
            env.Snapshot = Ctx(2, uid: "10086", clientId: "cid");
            sink.Write(new List<LogRecord>
            {
                Rec("late pre-login line", ctxVer: 1),
                Rec("after login", ctxVer: 2),
            });

            Assert.AreEqual(3, got.Count);
            Assert.IsFalse(Text(got[1].Bytes).Contains("10086"),
                "迟到的登录前日志仍然要用 v1 的快照");
            StringAssert.Contains(@"""uid"":""10086""", Text(got[2].Bytes));
        }

        /// <summary>
        /// ⚠️ sink **绝不能抛异常** —— S1 的 PackLoop 把整个 while 包在一个 catch 里，
        /// 异常穿透过去打包线程就死了，本地卷也跟着停。
        /// </summary>
        [Test]
        public void Sink_NeverThrowsIntoPackThread()
        {
            // 吞掉的异常会走 LogSystem.InternalError → Debug.LogError（全局硬约束 6）。
            LogAssert.ignoreFailingMessages = true;

            FakeEnvironment env = new FakeEnvironment { Snapshot = FullCtx() };
            LogBatchSink sink = new LogBatchSink(env, p => { throw new InvalidOperationException("disk full"); });

            Assert.DoesNotThrow(() => sink.Write(new List<LogRecord> { Rec("x") }));
        }

        /// <summary>handler 缺席（08 号票还没接上 outbox）时照常工作，不炸。</summary>
        [Test]
        public void Sink_WithoutHandler_IsHarmless()
        {
            FakeEnvironment env = new FakeEnvironment { Snapshot = FullCtx() };
            LogBatchSink sink = new LogBatchSink(env, null);

            Assert.DoesNotThrow(() => sink.Write(new List<LogRecord> { Rec("x") }));
            Assert.DoesNotThrow(() => sink.Persist());
        }

        /// <summary>sink 同时实现 C4 的 ILogBatchSink 与票 06 新增的可选接口 ILogBatchSinkPersist。</summary>
        [Test]
        public void Sink_ImplementsBothContractInterfaces()
        {
            LogBatchSink sink = new LogBatchSink(new FakeEnvironment(), null);

            Assert.IsInstanceOf<ILogBatchSink>(sink);
            Assert.IsInstanceOf<ILogBatchSinkPersist>(sink, "S1 用 as 探测这个接口");
        }
    }
}
