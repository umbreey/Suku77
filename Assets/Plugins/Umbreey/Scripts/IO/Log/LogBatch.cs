using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace Umbreey
{
    /// <summary>
    /// 一个批次的序列化产物：**payload_raw**（术语见 CONTEXT.md）——
    /// 批次序列化后、gzip 压缩前的原始字节。签名与摘要都基于它，
    /// 落盘（票 08）也直接写它，**中途绝不反序列化再序列化**。
    /// <para>
    /// 另外带上"这批是源列表里的哪一段"与"用的是哪个 CtxVer" ——
    /// 它们不进 JSON，只为让切批规则可被外部观测（也就可被测试钉住）。
    /// </para>
    /// </summary>
    internal sealed class LogBatchPayload
    {
        /// <summary>payload_raw。UTF-8 明文，无 BOM，单行无缩进。</summary>
        internal readonly byte[] Bytes;

        /// <summary>本批第一条记录在源列表里的下标。</summary>
        internal readonly int Offset;

        /// <summary>本批**实际写进 logs 数组**的条数（敏感记录已被剔除，不计入）。</summary>
        internal readonly int Count;

        /// <summary>本批所属的上下文版本号（契约 C2）。一个批次只有一个值。</summary>
        internal readonly int CtxVer;

        internal LogBatchPayload(byte[] bytes, int offset, int count, int ctxVer)
        {
            Bytes = bytes;
            Offset = offset;
            Count = count;
            CtxVer = ctxVer;
        }
    }

    /// <summary>
    /// 批次序列化与切批（S3 / 票 07 / specs 0003）。
    ///
    /// <para>
    /// **全部是纯函数**：给定记录、上下文快照与配置，产出确定的字节序列。
    /// 不碰 Unity API（<see cref="LogType"/> 只是个枚举，取值不触发引擎调用）、
    /// 不碰文件系统、不读全局状态，因此既能跑在打包线程上（全局硬约束 1），
    /// 也能在 EditMode 里直接测。
    /// </para>
    ///
    /// <para>
    /// ⚠️ **为什么逐字段手动写，而不是把一个对象丢给序列化库**（票 07 / specs 0003）：
    /// 把对象整体交出去，字段顺序就取决于反射顺序 / 特性 / 库版本，不由我们掌控；
    /// 而签名对字节敏感，任何重排、转义差异、空格增删都会让签名失效，
    /// 失败现象是"所有请求 code=2"，没有其他线索。
    /// **确定性由"逐字段手动写"保证，与用哪个库无关** —— 所以转义、控制字符、
    /// 代理对这些最容易写错的部分交给 Newtonsoft（Umbreey 已经在用，
    /// 见 BlackboardSerialize.cs），我们只决定"写哪些字段、按什么顺序写"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 所有阈值一律从 <see cref="LogConfig"/> 取（唯一事实源是参数总表），
    /// 本文件里不出现任何阈值字面量。
    /// </para>
    /// </summary>
    internal static class LogBatch
    {
        // ────────────────────────────────────────────────────────────────────────────────
        //  协议常量（协议 §3.5）。它们**不是可调阈值**，所以不进 LogConfig：
        //  改它们等于改协议，而不是调策略。
        // ────────────────────────────────────────────────────────────────────────────────

        /// <summary>协议版本，固定 1。</summary>
        internal const int ProtocolVersion = 1;

        /// <summary>
        /// 目标日志流的逻辑名，固定 "client"。
        /// <para>
        /// 实际取值来自 <see cref="LogSystem.Init"/> 的 topic 参数（契约 C5），
        /// 本常量只是宿主没传时的兜底 —— 空 topic 会让整批 code=1，
        /// 那是"配置漏了"而不是"该发空值"。
        /// </para>
        /// </summary>
        internal const string DefaultTopic = "client";

        /// <summary>截断标记的前半段。</summary>
        private const string MarkerHead = "...[truncated ";

        /// <summary>截断标记的后半段。整个标记是纯 ASCII，字符数 == 字节数。</summary>
        private const string MarkerTail = " bytes]";

        /// <summary>
        /// 标记长度求不动点的最大轮数。丢弃字节数的位数最多 10 位，
        /// 每轮至少让预算严格变大一次，12 轮是带足余量的上界。
        /// </summary>
        private const int MarkerFixpointRounds = 12;

        // ────────────────────────────────────────────────────────────────────────────────
        //  切批（specs 0003「切批规则」—— 本模块的核心职责）
        // ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 把打包线程一次交出来的整段记录切成若干批次，并逐批序列化成 payload_raw。
        ///
        /// <para>
        /// 契约 C4：<c>ILogBatchSink.Write</c> 收到的是**整个列表**，长度不受控，
        /// 可能远超一个批次装得下的量。**逐条组批的职责完全归本模块**，S1 不做任何分组。
        /// </para>
        ///
        /// <para>按顺序遍历，命中以下任一条件就封当前批、开新批：</para>
        /// <list type="number">
        ///   <item>条数达到 <see cref="LogConfig.BatchMaxCount"/>；</item>
        ///   <item>
        ///     **按最终序列化字节**累计达到 <see cref="LogConfig.BatchMaxBytes"/> ——
        ///     不是按 <see cref="LogRecord.Size"/>。<c>Size</c> 是采集侧的口径，
        ///     只用于攒批触发和背压，**不能用来控制批次预算**：它不覆盖 JSON 转义膨胀
        ///     （一个控制字符 1 字节变 6 字节）、<c>fields</c> 的重复 key、以及批头开销。
        ///     这里累加的是"这一批真正会被写出去的那些字节"，见 <see cref="EnvelopeByteCount"/>。
        ///   </item>
        ///   <item>
        ///     **<see cref="LogRecord.CtxVer"/> 发生变化**（契约 C2）—— 一个批次只能包含
        ///     同一上下文版本的记录，否则登录前产生的日志会被打上登录后的 uid，
        ///     凭空归到某个玩家名下。这是 C2 存在的全部意义。
        ///   </item>
        /// </list>
        ///
        /// <para>
        /// <see cref="LogRecord.Internal"/> **不影响切批**，正常混在批次里（全局硬约束 6）。
        /// <see cref="LogRecord.Sensitive"/> 则整条跳过（全局硬约束 7）：S1 的
        /// <c>AppendUploadable</c> 已经拦过一道，这里是字节产生之前的最后一道闸。
        /// </para>
        ///
        /// <para>
        /// 单条记录自己就超过 <see cref="LogConfig.BatchMaxBytes"/> 时**照发不误**（独占一批），
        /// 不丢弃也不死循环：msg 与 stack 都已各自截断，最坏情况仍远低于协议 1MB 的硬限，
        /// 而丢弃等于让最该看的那条日志消失。
        /// </para>
        /// </summary>
        /// <param name="records">打包线程交出来的整段记录，本方法只读不改。</param>
        /// <param name="resolveContext">
        /// CtxVer → 该版本对应的快照。**不是"当前最新的快照"**（specs 0003）——
        /// 批头必须用这一批记录所属的那个版本，见 <see cref="LogBatchContextCache"/>。
        /// </param>
        /// <param name="appId">批头必填的 app_id，与 X-App-Id 一致。</param>
        /// <param name="topic">CLS topic；空串时回落到 <see cref="DefaultTopic"/>。</param>
        /// <param name="cfg">阈值来源。null 时用参数总表的默认值。</param>
        internal static List<LogBatchPayload> Build(
            IReadOnlyList<LogRecord> records,
            Func<int, LogContextSnapshot> resolveContext,
            string appId,
            string topic,
            LogConfig cfg)
        {
            List<LogBatchPayload> result = new List<LogBatchPayload>();
            if (records == null || records.Count == 0)
            {
                return result;
            }

            LogConfig config = cfg ?? new LogConfig();
            string effectiveAppId = appId ?? "";
            string effectiveTopic = string.IsNullOrEmpty(topic) ? DefaultTopic : topic;

            // 配置被写坏时也不能死循环：一批至少放得下一条。
            int maxCount = config.BatchMaxCount > 0 ? config.BatchMaxCount : 1;
            int maxBytes = config.BatchMaxBytes;

            List<string> entries = new List<string>(maxCount);
            int i = 0;

            while (i < records.Count)
            {
                int ctxVer = records[i].CtxVer;
                LogContextSnapshot ctx = ResolveOrEmpty(resolveContext, ctxVer);

                entries.Clear();
                int start = i;

                // 预算的起点是**空批次的完整字节数**（批头 + "logs":[] + 收尾），
                // 之后每条只加"条目字节 + 一个逗号"。这样累加值与最终 GetBytes 的长度
                // 逐字节相等，不是估算。
                int total = EnvelopeByteCount(ctx, effectiveAppId, effectiveTopic);

                while (i < records.Count && entries.Count < maxCount)
                {
                    LogRecord record = records[i];

                    // ── CtxVer 切批：一个批次只能包含同一上下文版本的记录（契约 C2）──
                    if (record.CtxVer != ctxVer)
                    {
                        break;
                    }

                    // 敏感记录只落本地卷、不进批次（全局硬约束 7）。
                    // 跳过它不影响切批位置，因为它根本不产生字节。
                    if (record.Sensitive)
                    {
                        i++;
                        continue;
                    }

                    string entry = ComposeEntry(record, config);
                    int add = Utf8ByteCount(entry) + (entries.Count > 0 ? 1 : 0);

                    // 第一条无条件收下：单条超预算也要发，不能丢也不能空转。
                    if (entries.Count > 0 && total + add > maxBytes)
                    {
                        break;
                    }

                    entries.Add(entry);
                    total += add;
                    i++;
                }

                // 整段都是敏感记录时不产出空批次 —— 协议要求 logs 至少 1 条，
                // 空 logs 会让整批 code=1。
                if (entries.Count == 0)
                {
                    continue;
                }

                string document = ComposeDocument(ctx, effectiveAppId, effectiveTopic, entries);
                result.Add(new LogBatchPayload(
                    Encoding.UTF8.GetBytes(document), start, entries.Count, ctxVer));
            }

            return result;
        }

        /// <summary>
        /// 序列化**一整批**（调用方已经切好批）。给 08 号票补传、以及测试直接构造用。
        /// <para>
        /// ⚠️ 它不做任何切批检查：传进来的记录必须已经是同一个 CtxVer。
        /// </para>
        /// </summary>
        internal static byte[] Serialize(
            IReadOnlyList<LogRecord> records,
            LogContextSnapshot ctx,
            string appId,
            string topic,
            LogConfig cfg)
        {
            LogConfig config = cfg ?? new LogConfig();
            LogContextSnapshot snapshot = ctx ?? LogContextSnapshot.Empty;
            string effectiveTopic = string.IsNullOrEmpty(topic) ? DefaultTopic : topic;

            List<string> entries = new List<string>(records != null ? records.Count : 0);
            if (records != null)
            {
                for (int i = 0; i < records.Count; i++)
                {
                    if (records[i].Sensitive)
                    {
                        continue;
                    }

                    entries.Add(ComposeEntry(records[i], config));
                }
            }

            return Encoding.UTF8.GetBytes(
                ComposeDocument(snapshot, appId ?? "", effectiveTopic, entries));
        }

        // ────────────────────────────────────────────────────────────────────────────────
        //  序列化（协议 §3.5）
        // ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 批头 + logs 数组，逐字段手动写。**字段顺序就是协议 §3.5 表格的顺序**，
        /// 由这段代码的行序决定，与反射、特性、库版本都无关。
        ///
        /// <para>
        /// 已经序列化好的条目用 <c>WriteRawValue</c> 原样嵌进 logs 数组 ——
        /// 这样"切批时量到的字节"和"最终写出去的字节"是同一份东西，不可能出现分歧；
        /// 且条目本身仍然是 Newtonsoft 写出来的，转义没有绕过库。
        /// **这不是反序列化再序列化**：字节从产生到落盘只经手一次。
        /// </para>
        ///
        /// <para>
        /// 拿不到值的必填字段一律写**空字符串**，不省略、不写 null（协议 §3.5：
        /// "必填指字段必须存在，不指值必须非空"）。快照侧已由构造函数兜底成空串（契约 C2），
        /// 这里不必再判空。
        /// </para>
        /// </summary>
        private static string ComposeDocument(
            LogContextSnapshot ctx, string appId, string topic, List<string> entries)
        {
            StringBuilder sb = new StringBuilder(256);
            using (StringWriter text = new StringWriter(sb, CultureInfo.InvariantCulture))
            using (JsonTextWriter writer = NewWriter(text))
            {
                writer.WriteStartObject();

                writer.WritePropertyName("v");
                writer.WriteValue(ProtocolVersion);

                writer.WritePropertyName("app_id");
                writer.WriteValue(appId);

                writer.WritePropertyName("client_id");
                writer.WriteValue(ctx.ClientId);

                writer.WritePropertyName("uid");
                writer.WriteValue(ctx.Uid);

                writer.WritePropertyName("channel");
                writer.WriteValue(ctx.Channel);

                // Application.version 原值，不是 App.Version（后者把 '.' 换成了 '_'）。
                writer.WritePropertyName("app_version");
                writer.WriteValue(ctx.AppVersion);

                writer.WritePropertyName("res_version");
                writer.WriteValue(ctx.ResVersion);

                writer.WritePropertyName("platform");
                writer.WriteValue(ctx.Platform);

                writer.WritePropertyName("device");
                writer.WriteValue(ctx.Device);

                writer.WritePropertyName("topic");
                writer.WriteValue(topic);

                // ── tags：批次级标签，4 个 kv（specs/0000-index「批次字段归属」补记）──
                //
                // env / gpu / system / trace_id 都是**批次内不变量**。协议 §3.5 明确
                // 「tags 批内所有日志共用……服务端会展开复制到每条日志」，所以放这里
                // 和逐条写进 fields 在 CLS 侧的检索能力**完全一样**，字节却只写一遍 ——
                // 真机实测逐条重复占 outbox 原始字节的 10%（一个批次里 system 重复 1271 次）。
                //
                // ⚠️ **协议上限 5 个 kv，超 5 个整批返回 code=1**（整批被拒、只能等 TTL 淘汰）。
                // 现在用掉 4 个，只剩 1 个余量：再加批次级维度之前先回 0000-index 确认。
                //
                // ⚠️ **kv 顺序固定为 env → gpu → system → trace_id**，由下面这几行的行序
                // 决定，不经过任何字典/哈希 —— 同样的输入永远产出同样的字节，签名才立得住。
                // 这个顺序的来历：env 留在原位（改动前它是唯一的 kv），新上提的三个按 key
                // 的字典序排在后面。没有别的含义，但**不要重排**。
                writer.WritePropertyName("tags");
                writer.WriteStartObject();

                writer.WritePropertyName("env");
                writer.WriteValue(ctx.Env);

                writer.WritePropertyName("gpu");
                writer.WriteValue(ctx.Gpu);

                writer.WritePropertyName("system");
                writer.WriteValue(ctx.System);

                writer.WritePropertyName("trace_id");
                writer.WriteValue(ctx.TraceId);

                writer.WriteEndObject();

                writer.WritePropertyName("logs");
                writer.WriteStartArray();
                for (int i = 0; i < entries.Count; i++)
                {
                    writer.WriteRawValue(entries[i]);
                }

                writer.WriteEndArray();

                writer.WriteEndObject();
            }

            return sb.ToString();
        }

        /// <summary>
        /// 一条日志条目（协议 §3.5「条目字段」）：ts / level / msg / fields。
        ///
        /// <para>
        /// ⚠️ <c>ts</c> 用 <see cref="LogRecord.TsUnixMs"/> 的**原始值**，
        /// 即采集回调里取的 <c>DateTime.UtcNow</c>，**绝不做任何时钟校正**（全局硬约束 5 / ADR-0002）：
        /// X-Timestamp 一旦被校正就永远落在服务端窗口内，<c>rt - ts</c> 是识别时钟异常设备的
        /// 仅剩线索，在客户端"顺手修正" ts 会把它一起抹掉。
        /// </para>
        /// <para>
        /// ⚠️ 单位陷阱：<c>ts</c> 是**毫秒**（13 位），X-Timestamp 是**秒**（10 位），两者不能混。
        /// </para>
        /// <para>
        /// <c>fields</c> **只剩 <c>stack</c> 一个 key**：采集走 hook 拿不到结构化字段
        /// （ADR-0002），条目级真正逐条变化的就只有它；gpu / system / trace_id 是批次内
        /// 不变量，已经上提到批次级 <c>tags</c>（见 <see cref="ComposeDocument"/>）。
        /// </para>
        /// <para>
        /// <c>fields</c> 这个对象**恒定写出，stack 为空时也写成空串**，不改成"空就省略" ——
        /// 简单确定优先：批次格式可预期，空值 kv 服务端本来就会自行丢弃，
        /// 省掉它换不来可观的字节（真正的 10% 在上提那三个值上），却多一条分支。
        /// 值**是字符串**，避免不同客户端序列化出不同类型导致 CLS 侧索引冲突。
        /// </para>
        /// </summary>
        private static string ComposeEntry(LogRecord record, LogConfig cfg)
        {
            // msg 与 stack **各自独立截断**：协议只规定了 msg 上限，
            // 但一个递归爆栈的堆栈能有几 MB，不单独截就能靠单条撑破整批预算 →
            // 整批 code=1 被拒 → 只能等 TTL 淘汰，在此之前一直重试白耗预算。
            string msg = TruncateUtf8(record.Msg, cfg.MsgMaxBytes);
            string stack = TruncateUtf8(record.Stack, cfg.StackMaxBytes);

            StringBuilder sb = new StringBuilder(128);
            using (StringWriter text = new StringWriter(sb, CultureInfo.InvariantCulture))
            using (JsonTextWriter writer = NewWriter(text))
            {
                writer.WriteStartObject();

                writer.WritePropertyName("ts");
                writer.WriteValue(record.TsUnixMs);

                writer.WritePropertyName("level");
                writer.WriteValue(LevelName(record.Type));

                writer.WritePropertyName("msg");
                writer.WriteValue(msg);

                writer.WritePropertyName("fields");
                writer.WriteStartObject();

                writer.WritePropertyName("stack");
                writer.WriteValue(stack);

                writer.WriteEndObject();

                writer.WriteEndObject();
            }

            return sb.ToString();
        }

        /// <summary>
        /// 一个 logs 为空的批次占多少字节。切批预算以它为起点，
        /// 之后每条加"条目字节 + 一个逗号"，得到的就是最终字节数本身。
        /// </summary>
        private static int EnvelopeByteCount(LogContextSnapshot ctx, string appId, string topic)
        {
            return Utf8ByteCount(ComposeDocument(ctx, appId, topic, EmptyEntries));
        }

        private static readonly List<string> EmptyEntries = new List<string>();

        /// <summary>
        /// 统一的 writer 配置 —— 确定性全靠它和"逐字段手动写"两件事。
        /// <para>
        /// <c>Formatting.None</c>：无缩进、无多余空格。
        /// <c>StringEscapeHandling.Default</c>：只转义控制字符与引号反斜杠，
        /// **非 ASCII 原样输出 UTF-8**（转 \uXXXX 是 <c>EscapeNonAscii</c> 这个可选值才有的行为，
        /// 不是默认值）。<c>InvariantCulture</c>：数字格式不随设备语言变化。
        /// 三项都显式写出来，不依赖库的默认值 —— 默认值会随库版本变，而签名对字节敏感。
        /// </para>
        /// </summary>
        private static JsonTextWriter NewWriter(TextWriter text)
        {
            return new JsonTextWriter(text)
            {
                Formatting = Formatting.None,
                StringEscapeHandling = StringEscapeHandling.Default,
                Culture = CultureInfo.InvariantCulture,
                CloseOutput = false,
            };
        }

        /// <summary>
        /// <see cref="LogType"/> 原值字符串，5 种，**不做映射**（协议 §3.5：
        /// level 取值由接入方自定义，服务端不校验、原样透传）。
        /// <para>
        /// 用 switch 而不是 ToString()：枚举的 ToString 走反射与装箱，
        /// 而这里是每条日志都要走一遍的热路径。
        /// </para>
        /// </summary>
        internal static string LevelName(LogType type)
        {
            switch (type)
            {
                case LogType.Error:
                    return "Error";
                case LogType.Assert:
                    return "Assert";
                case LogType.Warning:
                    return "Warning";
                case LogType.Log:
                    return "Log";
                case LogType.Exception:
                    return "Exception";
                default:
                    return type.ToString();
            }
        }

        // ────────────────────────────────────────────────────────────────────────────────
        //  截断（specs 0003「截断」）
        // ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 按 **UTF-8 字节**截断，保头，尾部补 <c>...[truncated N bytes]</c>。
        ///
        /// <para>
        /// ⚠️ **标记本身算在上限内**：截到上限再追加后缀会重新超限，
        /// 正确做法是截到「上限 − 标记长度」。而标记长度又取决于丢弃字节数的位数，
        /// 丢弃量又取决于截在哪里 —— 这是个不动点，下面先求预算再夹字符边界，
        /// 边界回退后若位数变长就再退一格，直到 <c>保留字节 + 标记长度 ≤ 上限</c>。
        /// </para>
        ///
        /// <para>
        /// ⚠️ **不得把多字节字符或代理对从中间劈开**（中文 3 字节、emoji 是代理对占 4 字节）——
        /// 劈开会让 UTF-8 编码器吐出替换字符，甚至让截断后的内容不再是原文。
        /// 边界由 <see cref="Utf8PrefixChars"/> 保证。
        /// </para>
        ///
        /// <para>
        /// 保头而不是保尾：异常类型和最内层栈帧都在前面，那才是排查要看的部分。
        /// </para>
        ///
        /// <para>
        /// 退化情形：上限小到连一个标记都放不下时（正常配置下不可能，
        /// <see cref="LogConfig.MsgMaxBytes"/> 是 64KB），退成"无标记的边界安全硬截断" ——
        /// 宁可少一句说明，也不能超限。
        /// </para>
        /// </summary>
        internal static string TruncateUtf8(string value, int maxBytes)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value ?? "";
            }

            if (maxBytes <= 0)
            {
                return "";
            }

            int total = Utf8ByteCount(value);
            if (total <= maxBytes)
            {
                return value;
            }

            // 1) 先求"可保留字节预算"。起点用最长可能的标记（丢弃量 = 全长），
            //    随后每轮用更小的丢弃量重算，预算单调变大直到稳定。
            int budget = maxBytes - MarkerLength(total);
            if (budget < 0)
            {
                budget = 0;
            }

            for (int round = 0; round < MarkerFixpointRounds; round++)
            {
                int next = maxBytes - MarkerLength(total - budget);
                if (next <= budget)
                {
                    break;
                }

                budget = next;
            }

            // 2) 夹到字符边界，再按**真实**丢弃量算标记；位数变长就退一格重来。
            while (true)
            {
                int keptBytes;
                int keptChars = Utf8PrefixChars(value, budget, out keptBytes);
                string marker = Marker(total - keptBytes);

                if (keptBytes + marker.Length <= maxBytes)
                {
                    return value.Substring(0, keptChars) + marker;
                }

                if (budget <= 0)
                {
                    // 上限连标记都装不下：退成无标记的边界安全硬截断。
                    int hardBytes;
                    int hardChars = Utf8PrefixChars(value, maxBytes, out hardBytes);
                    return value.Substring(0, hardChars);
                }

                budget = keptBytes > 0 ? keptBytes - 1 : 0;
            }
        }

        /// <summary>截断标记。纯 ASCII，字符数 == 字节数。</summary>
        private static string Marker(int droppedBytes)
        {
            return MarkerHead + droppedBytes.ToString(CultureInfo.InvariantCulture) + MarkerTail;
        }

        /// <summary>不构造字符串地算标记长度 —— 求不动点时要反复用到。</summary>
        private static int MarkerLength(int droppedBytes)
        {
            return MarkerHead.Length + DigitCount(droppedBytes) + MarkerTail.Length;
        }

        private static int DigitCount(int value)
        {
            if (value <= 0)
            {
                return 1;
            }

            int digits = 0;
            while (value > 0)
            {
                digits++;
                value /= 10;
            }

            return digits;
        }

        /// <summary>
        /// 最长的、UTF-8 字节数不超过 <paramref name="maxBytes"/> 的前缀，
        /// 返回它的**字符数**（UTF-16 code unit 数），并带出它的**字节数**。
        ///
        /// <para>
        /// 逐 code point 前进，代理对整对进出，因此永远不会切出半个字符：
        /// &lt;0x80 → 1 字节；&lt;0x800 → 2；完整代理对 → 4（整对消耗 2 个 char）；
        /// 其余 → 3。孤立代理按 3 计 —— UTF-8 编码器会把它替换成 U+FFFD（3 字节），
        /// 按 3 计才能让字节数与 <c>Encoding.UTF8.GetByteCount</c> 严格一致。
        /// </para>
        /// </summary>
        private static int Utf8PrefixChars(string value, int maxBytes, out int bytes)
        {
            int used = 0;
            int index = 0;

            while (index < value.Length)
            {
                char c = value[index];
                int size;
                int step;

                if (c < 0x80)
                {
                    size = 1;
                    step = 1;
                }
                else if (c < 0x800)
                {
                    size = 2;
                    step = 1;
                }
                else if (char.IsHighSurrogate(c)
                    && index + 1 < value.Length
                    && char.IsLowSurrogate(value[index + 1]))
                {
                    size = 4;
                    step = 2;
                }
                else
                {
                    size = 3;
                    step = 1;
                }

                if (used + size > maxBytes)
                {
                    break;
                }

                used += size;
                index += step;
            }

            bytes = used;
            return index;
        }

        private static int Utf8ByteCount(string value)
        {
            return string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);
        }

        private static LogContextSnapshot ResolveOrEmpty(
            Func<int, LogContextSnapshot> resolve, int ctxVer)
        {
            if (resolve == null)
            {
                return LogContextSnapshot.Empty;
            }

            return resolve(ctxVer) ?? LogContextSnapshot.Empty;
        }
    }

    /// <summary>
    /// CtxVer → 该版本的上下文快照。
    ///
    /// <para>
    /// **为什么需要它**：specs 0003 要求"批头必须用这一批记录所属的那个 CtxVer 对应的快照，
    /// 不是当前最新的快照"，而 S1 只暴露 <see cref="LogSystem.Context"/>（最新的那个）。
    /// 打包线程每轮都会看一眼最新快照并记进环形缓冲，于是"上一个版本"几乎总还在手上 ——
    /// 登录前后各有大量日志，两次 Write 之间连跳两个版本是极少数情况。
    /// </para>
    ///
    /// <para>
    /// **配不上时怎么办**（<see cref="Degrade"/>）：把随 Set* 变化的那几个字段清成空串，
    /// 设备级字段（app_version / platform / device / gpu / system / client_id / trace_id，
    /// 它们在 Init 之后就不再变）照常带上。这样最坏情况是"少了 uid / env / res_version"，
    /// 而**绝不会**出现"把登录前的日志打上登录后的 uid"——后者才是 C2 要挡的事故。
    /// 协议 §3.5 也明确：拿不到值就传空串，不省略字段。
    /// </para>
    ///
    /// <para>⚠️ 非线程安全，只在打包线程上使用（它是唯一调用 <c>ILogBatchSink.Write</c> 的线程）。</para>
    /// </summary>
    internal sealed class LogBatchContextCache
    {
        /// <summary>
        /// 记住最近几个版本。取 8 是因为一次会话里的版本跃迁屈指可数
        /// （Init、拿到渠道、热更完成、登录、登出），而每多一格只多一个引用。
        /// </summary>
        internal const int Capacity = 8;

        private readonly LogContextSnapshot[] m_Ring = new LogContextSnapshot[Capacity];
        private int m_Next;

        /// <summary>把当前快照记下来。同一个版本重复喂进来不会挤掉别的格子。</summary>
        internal void Observe(LogContextSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            for (int i = 0; i < m_Ring.Length; i++)
            {
                if (m_Ring[i] != null && m_Ring[i].Version == snapshot.Version)
                {
                    return;
                }
            }

            m_Ring[m_Next] = snapshot;
            m_Next = (m_Next + 1) % m_Ring.Length;
        }

        /// <summary>
        /// 解析出该批次该用的批头快照。命中不了就走 <see cref="Degrade"/>。
        /// </summary>
        internal LogContextSnapshot Resolve(int ctxVer, LogContextSnapshot latest)
        {
            if (latest != null && latest.Version == ctxVer)
            {
                return latest;
            }

            for (int i = 0; i < m_Ring.Length; i++)
            {
                if (m_Ring[i] != null && m_Ring[i].Version == ctxVer)
                {
                    return m_Ring[i];
                }
            }

            return Degrade(latest, ctxVer);
        }

        /// <summary>
        /// 降级快照：保留设备级字段，把**会随 Set* 变化**的四个字段
        /// （uid / env / res_version / channel）清成空串。
        /// <para>
        /// 宁可少几个维度，也不能张冠李戴 —— 一条日志被错误地归到某个玩家名下，
        /// 是查不出来也洗不掉的脏数据。
        /// </para>
        /// </summary>
        internal static LogContextSnapshot Degrade(LogContextSnapshot latest, int ctxVer)
        {
            LogContextSnapshot source = latest ?? LogContextSnapshot.Empty;
            return new LogContextSnapshot(
                ctxVer,
                null,
                null,
                null,
                null,
                source.AppVersion,
                source.Platform,
                source.Device,
                source.Gpu,
                source.System,
                source.ClientId,
                source.TraceId);
        }
    }

    /// <summary>
    /// 批次产出的去向。08 号票接上 outbox 落盘；在那之前挂 null 就是"打包但不落盘"。
    /// </summary>
    internal delegate void LogBatchHandler(LogBatchPayload payload);

    /// <summary>
    /// 批头需要的三样运行期数据的来源。抽成接口只为一件事：
    /// 让 <see cref="LogBatchSink"/> 能在 EditMode 里被完整测试，
    /// 而不必去调 <see cref="LogSystem.Init"/>（它会取 UmbreeySDK 的设备信息，
    /// 而 UmbreeySDK 的静态构造会 new GameObject）。
    /// </summary>
    internal interface ILogBatchEnvironment
    {
        string AppId { get; }

        string Topic { get; }

        LogContextSnapshot Context { get; }

        LogConfig Config { get; }
    }

    /// <summary>真实来源：S1 在 <see cref="LogSystem.Init"/> 里存下的那几个值。</summary>
    internal sealed class LogSystemBatchEnvironment : ILogBatchEnvironment
    {
        internal static readonly LogSystemBatchEnvironment Instance = new LogSystemBatchEnvironment();

        private LogSystemBatchEnvironment()
        {
        }

        public string AppId
        {
            get { return LogSystem.AppId ?? ""; }
        }

        public string Topic
        {
            get { return LogSystem.Topic ?? ""; }
        }

        public LogContextSnapshot Context
        {
            get { return LogSystem.Context; }
        }

        public LogConfig Config
        {
            get { return LogSystem.Config; }
        }
    }

    /// <summary>
    /// 契约 C4 的 <see cref="ILogBatchSink"/> 实现：收下打包线程交出的整段记录，
    /// 切批、序列化，把每个 payload_raw 交给 <see cref="LogBatchHandler"/>。
    ///
    /// <para>
    /// ⚠️ 跑在**打包线程**上，不得调用任何 Unity API（全局硬约束 1），
    /// 也**不得抛异常** —— S1 的 PackLoop 把整个 while 包在一个 catch 里，
    /// 异常穿透过去会让打包线程直接死掉，本地卷也跟着停。所以这里整段吞异常，
    /// 只留一条 Internal 诊断日志（全局硬约束 6）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 传进来的列表由 S1 每批新建、交出后不再改动（契约 C4 补记），可以安全持有，
    /// 因此本类不做任何拷贝。
    /// </para>
    /// </summary>
    internal sealed class LogBatchSink : ILogBatchSink, ILogBatchSinkPersist
    {
        private readonly ILogBatchEnvironment m_Env;
        private readonly LogBatchHandler m_Handler;
        private readonly LogBatchContextCache m_Cache = new LogBatchContextCache();

        internal LogBatchSink(LogBatchHandler handler)
            : this(LogSystemBatchEnvironment.Instance, handler)
        {
        }

        internal LogBatchSink(ILogBatchEnvironment env, LogBatchHandler handler)
        {
            m_Env = env ?? LogSystemBatchEnvironment.Instance;
            m_Handler = handler;
        }

        public void Write(IReadOnlyList<LogRecord> records)
        {
            if (records == null || records.Count == 0)
            {
                return;
            }

            try
            {
                LogContextSnapshot latest = m_Env.Context;
                m_Cache.Observe(latest);

                List<LogBatchPayload> batches = LogBatch.Build(
                    records,
                    ctxVer => m_Cache.Resolve(ctxVer, latest),
                    m_Env.AppId,
                    m_Env.Topic,
                    m_Env.Config);

                if (m_Handler == null)
                {
                    return;
                }

                for (int i = 0; i < batches.Count; i++)
                {
                    m_Handler(batches[i]);
                }
            }
            catch (Exception e)
            {
                // 打包线程不能因为一次序列化失败就没了。
                LogSystem.InternalError("batch serialize failed: " + e);
            }
        }

        /// <summary>
        /// <see cref="ILogBatchSinkPersist"/>（票 06 新增的可选接缝）。
        ///
        /// <para>
        /// **本实现手里永远没有"没切完的尾巴"**：<see cref="Write"/> 会把整段记录
        /// 全部切完并逐批交给 handler，包括最后那个不满一批的尾巴。
        /// 所以这里没有东西可固化，是一个**有意为之的空实现**。
        /// </para>
        /// <para>
        /// 为什么不在这里攒着等下一次：攒批窗口（BatchLingerMs）是 S1 的职责，
        /// 它已经等过一轮才把记录交出来。S3 再攒一次等于把窗口叠加两遍，
        /// 而且会让一次会话最后几条日志（空闲时不再有新记录来推动）一直躺在内存里 ——
        /// 那恰恰是 outbox 存在的意义所要保护的字节。
        /// </para>
        /// <para>
        /// **08 号票落地后仍然是空实现，而且是成立的**：handler 侧是
        /// <see cref="LogOutbox.Submit(LogBatchPayload)"/>，它**同步**把这一批原子写进
        /// outbox（写 <c>.tmp</c> → Flush → <c>File.Move</c>）才返回，手里不留任何字节。
        /// 于是 <see cref="Write"/> 一返回，内存里的东西就已经全在磁盘上了 ——
        /// 票 06 的 pause 语义「内存现在必须变成文件」由此自动满足，没有缓冲要冲。
        /// </para>
        /// <para>
        /// 接缝仍然保留：将来若真在 handler 侧引入写缓冲（例如合并小批次），
        /// **必须在这里把它冲掉**，S1 的调用顺序（PersistSink）已经是最终形态。
        /// </para>
        /// </summary>
        public void Persist()
        {
        }
    }
}
