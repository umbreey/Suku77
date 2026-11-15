using System;
using System.Collections.Generic;
using UnityEngine;

namespace Umbreey
{
    /// <summary>
    /// 采集缓冲：A/B 双缓冲 + 背压。设计见 specs/0001「缓冲与打包线程」。
    ///
    /// <para>
    /// 线程模型：<see cref="Add"/> 由**任意线程**调用
    /// （logMessageReceivedThreaded 会从任意线程回调，TCPClient 就在 ThreadPool 线程上打日志）；
    /// <see cref="Swap"/> 只由打包线程调用。lock 只保护一次 Add 与一次换出，
    /// 打包线程拿到的是已经换出去的只读列表，处理全程无锁。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 本类会在打包线程上被调用，因此**不得调用任何 Unity API**（全局硬约束 1）。
    /// 时间戳等 Unity 相关的值在采集回调里就已经取好并放进 <see cref="LogRecord"/>。
    /// </para>
    ///
    /// <para>
    /// 背压：累计字节超过 LogConfig.BufferLimitBytes 时**丢最旧**的记录 ——
    /// 队列是 FIFO，最靠近崩溃的是最新的那几条，那才是排查现场最需要的。
    /// 丢弃计数由 <see cref="Swap"/> 交出，使用方在下次交换时写一行 dropped N records。
    /// ⚠️ 在缓冲里被丢的记录**还没写过本地卷**，两个接收端会同时失去它（ADR-0001）——
    /// 不要在任何地方宣称"被丢的旧记录仍然在本地卷里"，那只对已经交换出去的记录成立。
    /// </para>
    /// </summary>
    internal sealed class LogBuffer
    {
        /// <summary>
        /// 两个缓冲列表的初始容量。只影响启动期的几次扩容，不是可调阈值，故不进参数总表。
        /// </summary>
        private const int InitialCapacity = 256;

        /// <summary>只保护"一次 Add"与"一次换出"，不覆盖打包线程对换出列表的处理。</summary>
        private readonly object m_Lock = new object();

        /// <summary>背压上限，取自 LogConfig.BufferLimitBytes。Init 之后不再变，故直接存值。</summary>
        private readonly int m_LimitBytes;

        /// <summary>当前正在接收采集结果的缓冲。</summary>
        private List<LogRecord> m_Active = new List<LogRecord>(InitialCapacity);

        /// <summary>上一轮换出去、已被打包线程处理完的缓冲，等着被复用成下一个 <see cref="m_Active"/>。</summary>
        private List<LogRecord> m_Standby = new List<LogRecord>(InitialCapacity);

        /// <summary><see cref="m_Active"/> 里记录的 <see cref="LogRecord.Size"/> 累加值。</summary>
        private int m_ActiveBytes;

        /// <summary>自上次 <see cref="Swap"/> 以来因背压丢弃的条数。</summary>
        private int m_DroppedCount;

        /// <summary>
        /// 累计**进过缓冲**的记录条数，单调递增、永不回退 —— 这就是票 06 的「序号水位」。
        ///
        /// <para>
        /// 一条记录进缓冲就领一个号。<see cref="LogSystem.Flush"/> 记下调用那一刻的号 N，
        /// 等打包线程报告"号 ≤ N 的都处理完了"。用**序号**而不是布尔信号，是因为布尔信号在
        /// "上一轮超时之后确认才迟到"这种情形下会误满足下一轮 flush；水位是单调量，
        /// 迟到的那个确认带着旧的号，天然满足不了更高的目标（票 06）。
        /// </para>
        /// <para>
        /// ⚠️ **背压丢弃不回退序号**：被丢的记录已经领过号了。这是刻意的 ——
        /// 交换时报出去的水位涵盖它们，否则 Flush 会为几条永远不会落盘的记录一直等到超时。
        /// 因此水位的准确语义是"号 ≤ N 的记录都已**处理完毕**（写盘或被背压丢弃）"，
        /// 不是"都已写进卷"。
        /// </para>
        /// </summary>
        private long m_EnqueuedSeq;

        /// <param name="cfg">只读取 BufferLimitBytes；约定 Init 之后不再改动，故无需同步。</param>
        internal LogBuffer(LogConfig cfg)
        {
            m_LimitBytes = cfg.BufferLimitBytes;
        }

        /// <summary>
        /// 当前的序号水位：迄今为止进过缓冲的记录条数（见 <see cref="m_EnqueuedSeq"/>）。
        /// <para>
        /// <see cref="LogSystem.Flush"/> 在**调用那一刻**读它作为本轮的目标水位 ——
        /// 这正是"Flush 只保证调用那一刻已在缓冲里的记录"这条语义的落点：
        /// 之后到达的记录会拿到更大的号，不在本轮的保证范围内。
        /// </para>
        /// <para>任意线程可读，内部加锁。</para>
        /// </summary>
        internal long EnqueuedSeq
        {
            get { lock (m_Lock) { return m_EnqueuedSeq; } }
        }

        /// <summary>
        /// 追加一条记录。**任意线程**，内部加锁。超过背压上限时丢弃最旧的记录。
        ///
        /// <para>
        /// 刚加进来的这一条**永远不会被丢**：即使它自己就超过了上限（一条巨型堆栈），
        /// 也宁可让缓冲短暂超限，也不丢最新的那条 —— 背压的整个立意就是保住最新的记录。
        /// </para>
        /// </summary>
        internal void Add(LogRecord record)
        {
            lock (m_Lock)
            {
                m_Active.Add(record);
                m_ActiveBytes += record.Size;

                // 领号。**在背压丢弃之前**递增：被丢的记录也算"进过缓冲"，
                // 否则 Flush 会为它们死等（见 m_EnqueuedSeq 的注释）。
                m_EnqueuedSeq++;

                if (m_ActiveBytes <= m_LimitBytes)
                {
                    return;
                }

                // 从头（最旧）开始数出这一次要丢多少条，再一次 RemoveRange 掉。
                // 逐条 RemoveAt(0) 每次都要搬整个数组，持续超限时会退化成 O(n²)。
                int drop = 0;
                int freed = 0;
                while (m_ActiveBytes - freed > m_LimitBytes && drop < m_Active.Count - 1)
                {
                    freed += m_Active[drop].Size;
                    drop++;
                }

                if (drop > 0)
                {
                    m_Active.RemoveRange(0, drop);
                    m_ActiveBytes -= freed;
                    m_DroppedCount += drop;
                }
            }
        }

        /// <summary>
        /// 换出活动缓冲，返回这一轮要处理的记录。**只由打包线程调用。**
        /// 返回后活动缓冲被清空（下一条 Add 落进一个空列表），调用方独占返回的列表。
        ///
        /// <para>
        /// ⚠️ 返回的列表是**内部复用的**那两个之一：它只在下一次 <see cref="Swap"/> 之前有效。
        /// 打包线程必须在本轮内处理完（写卷 + 追加进批次累加器），不能把它存起来跨轮使用。
        /// 批次累加器持有的是逐条拷贝出去的 <see cref="LogRecord"/>（值类型），不受影响。
        /// </para>
        /// </summary>
        /// <param name="droppedCount">自上次交换以来因背压丢弃的条数。</param>
        internal IReadOnlyList<LogRecord> Swap(out int droppedCount)
        {
            long ignored;
            return Swap(out droppedCount, out ignored);
        }

        /// <summary>
        /// 同上，另外交出**交换那一刻的序号水位**（票 06）。
        ///
        /// <para>
        /// 打包线程处理完这一批之后拿这个号去 <see cref="LogFlushGate.ReportRound"/> 报告，
        /// 在等的 <see cref="LogSystem.Flush"/> 据此判定"我要的那些记录已经落地了"。
        /// </para>
        /// <para>
        /// ⚠️ 号必须在**换出的同一个锁段里**取，不能事后再读 <see cref="EnqueuedSeq"/>：
        /// 两者之间新到的记录会让报出去的水位**高于**这一批实际写出的内容，
        /// 那正是"假成功"——Flush 会认为一批还在缓冲里的记录已经落盘。
        /// </para>
        /// </summary>
        /// <param name="enqueuedSeq">交换那一刻的水位：本批记录的号全都 ≤ 它。</param>
        internal IReadOnlyList<LogRecord> Swap(out int droppedCount, out long enqueuedSeq)
        {
            lock (m_Lock)
            {
                enqueuedSeq = m_EnqueuedSeq;

                List<LogRecord> full = m_Active;

                m_Active = m_Standby;
                m_Active.Clear();
                m_Standby = full;

                m_ActiveBytes = 0;
                droppedCount = m_DroppedCount;
                m_DroppedCount = 0;

                return full;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════════════
    //  限流（票 05 / specs 0001「采集」的限流段）
    //
    //  为什么这几个类型住在 LogBuffer.cs 而不是自己一个文件：文件所有权表（specs/0000-index.md）
    //  把 S1 的文件逐个点了名，新建文件不在其列。而就归类而言这里也是对的 ——
    //  背压与限流是**采集入口上的两道闸**，谁都不碰 IO、不碰 Unity API、不读时钟：
    //
    //    · LogBuffer   已经进了缓冲、但缓冲装不下 → 丢**最旧**的（最靠近崩溃的是最新的）
    //    · LogThrottle 压根不让它进缓冲          → 丢**最新**的（配额已经用完了）
    //
    //  两者的丢弃方向相反，因为立意不同：背压在保现场，限流在保配额。
    // ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 当前处于哪一种抑制状态。**两层计数彼此独立，所以状态也必须是可以叠加的两位**
    /// （specs/0001「采集」、票 05「为什么是两层独立计数」）。
    ///
    /// <para>
    /// 用 <see cref="FlagsAttribute"/> 而不是 None/Exception/Session/Both 四个互斥值：
    /// 四值枚举得靠调用方自己维护"Both 是那两个的合取"这条不变量，而这正是本票最怕写错的地方 ——
    /// 异常触顶时报成"总量触顶"，会把排查方向从"谁在刷异常"直接带到"谁在写大日志"。
    /// </para>
    /// </summary>
    [Flags]
    internal enum LogSuppressState
    {
        /// <summary>没有任何一层触顶，全部放行。</summary>
        None = 0,

        /// <summary>
        /// 异常条数（<see cref="LogConfig.ExceptionLimitCount"/>）触顶。
        /// **只停 Error / Assert / Exception，Log / Warning 照常放行。**
        /// </summary>
        Exception = 1 << 0,

        /// <summary>
        /// 单会话总字节（<see cref="LogConfig.SessionLimitBytes"/>）触顶。**全停。**
        /// </summary>
        Session = 1 << 1,
    }

    /// <summary>
    /// 除了"这条放不放行"之外，还要不要额外插一行系统自产日志。
    /// <para>
    /// 触顶后**不能全静默**：CLS 里"日志突然断了"分不清是进程崩了还是被限流了，
    /// 而这两种情况的排查方向完全相反（票 05）。
    /// </para>
    /// </summary>
    internal enum LogThrottleNotice
    {
        /// <summary>不用插。</summary>
        None = 0,

        /// <summary>
        /// 触顶标记行。**在抑制状态发生变化的那一刻插一次**，同一个状态不会插第二次。
        /// </summary>
        Marker,

        /// <summary>
        /// 抑制期的心跳，每 <see cref="LogConfig.SuppressNoticeSec"/> 一条。
        /// </summary>
        Heartbeat,
    }

    /// <summary>
    /// <see cref="LogThrottle.Admit"/> 的全部输出。刻意做成一个值而不是几个 out 参数：
    /// 状态机的可测性来自"输入一串三元组、输出一串决定"，决定必须是能整体断言的东西。
    /// </summary>
    internal struct LogThrottleDecision
    {
        /// <summary>这条记录是否放行（进缓冲 → 进本地卷 / 批次累加器）。</summary>
        public bool Allow;

        /// <summary>是否还要额外插一行系统自产日志，插哪一种。</summary>
        public LogThrottleNotice Notice;

        /// <summary>决定作出之后的抑制状态。标记行与心跳的文案全靠它区分两层。</summary>
        public LogSuppressState State;

        /// <summary>自上一条标记 / 心跳以来被抑制的条数。心跳靠它说出"这 60 秒吞了多少"。</summary>
        public int SuppressedSinceNotice;

        /// <summary>本会话累计被抑制的条数。</summary>
        public long SuppressedTotal;
    }

    /// <summary>
    /// 限流状态机：**两层彼此独立的计数**，任一触顶只抑制它对应的那一类
    /// （specs/0001「采集」、票 05）。
    ///
    /// <para>
    /// | 层 | 阈值 | 触顶速度 | 触顶后抑制谁 |
    /// |---|---|---|---|
    /// | 异常条数 | <see cref="LogConfig.ExceptionLimitCount"/> | 每帧抛异常 → 十几秒 | 只停 Error/Assert/Exception |
    /// | 会话总字节 | <see cref="LogConfig.SessionLimitBytes"/> | 每帧一条 Info → 37 分钟 | 全停 |
    /// </para>
    ///
    /// <para>
    /// ⚠️ **两层不能合并成一个配额。** 合并之后，一个 <c>Update</c> 里的空引用会在几十秒内
    /// 吃掉全部配额，这次会话**后面所有正常日志**都被抑制 —— 那正是限流本身要避免的失效模式：
    /// 排查一次崩溃时，最需要的往往不是那一万条一模一样的异常，而是它前后的业务日志。
    /// 异常条数才是挡失控的实时闸，总量只是磁盘兜底，两者作用不同。
    /// </para>
    ///
    /// <para>
    /// ⚠️ **Internal 日志（全局硬约束 6）整条 bypass**：不计异常条数、不计会话字节、不被抑制。
    /// 否则触顶后连"我被限流了"这句话都发不出来 —— 标记行和心跳自己就是 Internal 日志。
    /// </para>
    ///
    /// <para>
    /// ⚠️ **本类不读时钟。** 时刻由 <see cref="Admit"/> 的参数注入（调用方传
    /// <see cref="LogSystem.UptimeMs"/>，即 Stopwatch 的单调时钟，全局硬约束 2）。
    /// 内部读时钟的话，那几条与 <see cref="LogConfig.SuppressNoticeSec"/> 相关的断言就只能靠
    /// 真的睡 60 秒来测 —— 那种测试既慢又必然变成偶发失败。
    /// </para>
    ///
    /// <para>
    /// 线程模型：<see cref="Admit"/> 由**任意线程**调用（采集回调在打日志的那个线程上同步回调），
    /// 内部加锁。不碰 IO、不碰 Unity API（<see cref="LogType"/> 只是枚举），
    /// 因此可以在 EditMode 里直接摆输入断输出。
    /// </para>
    /// </summary>
    internal sealed class LogThrottle
    {
        /// <summary>只保护一次 <see cref="Admit"/>。无竞争时代价可忽略。</summary>
        private readonly object m_Lock = new object();

        /// <summary>
        /// 阈值来源。持引用而不是构造时取值，与 <see cref="LogVolume"/> 一致。
        /// 约定 Init 之后不再改动实例，故跨线程只读无需同步。
        /// </summary>
        private readonly LogConfig m_Config;

        /// <summary>
        /// 已放行的 Error / Assert / Exception 条数。**独立于 <see cref="m_SessionBytes"/>** ——
        /// 它只被异常推动，一条 Info 再大也不会让它前进一格。
        /// </summary>
        private int m_ExceptionCount;

        /// <summary>
        /// 已放行的全部记录（含异常）的累计字节。**独立于 <see cref="m_ExceptionCount"/>** ——
        /// 它只被字节数推动，异常刷屏刷掉的是它的字节，但推动它的规则和阈值都是另一套。
        /// </summary>
        private long m_SessionBytes;

        private LogSuppressState m_State;

        private int m_SuppressedSinceNotice;

        private long m_SuppressedTotal;

        /// <summary>
        /// 上一条标记 / 心跳的**单调**时刻。心跳间隔从它算起，因此标记行也会重置它 ——
        /// 触顶那一瞬间刚说过一次，60 秒内不必再说第二遍。
        /// </summary>
        private long m_LastNoticeUptimeMs;

        /// <param name="cfg">读取 ExceptionLimitCount / SessionLimitBytes / SuppressNoticeSec。</param>
        internal LogThrottle(LogConfig cfg)
        {
            m_Config = cfg;
        }

        /// <summary>当前抑制状态。诊断与测试用。</summary>
        internal LogSuppressState State
        {
            get { lock (m_Lock) { return m_State; } }
        }

        /// <summary>已放行的异常条数。**只有异常推动它**，这是两层独立的可观测证据之一。</summary>
        internal int ExceptionCount
        {
            get { lock (m_Lock) { return m_ExceptionCount; } }
        }

        /// <summary>已放行的累计字节。**任何等级都推动它**，包括异常。</summary>
        internal long SessionBytes
        {
            get { lock (m_Lock) { return m_SessionBytes; } }
        }

        /// <summary>本会话累计被抑制的条数。</summary>
        internal long SuppressedTotal
        {
            get { lock (m_Lock) { return m_SuppressedTotal; } }
        }

        /// <summary>
        /// 判定一条记录放不放行，以及要不要额外插一行标记 / 心跳。
        ///
        /// <para>
        /// 顺序是有讲究的：**先判当前状态挡不挡它，再拿放行的那条去推计数**。
        /// 反过来（先记账再判定）会让配额虚耗在已经被抑制的记录上，
        /// "第 1001 条被抑制"就变成"第 1001 条被抑制且总量也被它推着涨"。
        /// </para>
        /// </summary>
        /// <param name="type">UnityEngine.LogType 原值。</param>
        /// <param name="size">这条记录的字节数（<see cref="LogRecord.Size"/>，已含堆栈）。</param>
        /// <param name="isInternal">日志系统自产的诊断日志，**整条 bypass**（全局硬约束 6）。</param>
        /// <param name="uptimeMs">单调时刻，由调用方注入 —— 本类不读时钟（全局硬约束 2）。</param>
        internal LogThrottleDecision Admit(LogType type, int size, bool isInternal, long uptimeMs)
        {
            lock (m_Lock)
            {
                // bypass：标记行与心跳自己就是 Internal 日志，它们要是也被计数、也会被抑制，
                // 触顶之后系统就再也说不出"我被限流了"。既不记账，也不受状态影响。
                if (isInternal)
                {
                    return Decide(true, LogThrottleNotice.None);
                }

                bool isException = IsException(type);

                // 会话总量触顶 → 全停；异常条数触顶 → 只挡异常，Log / Warning 照常走。
                bool blocked = (m_State & LogSuppressState.Session) != 0
                               || ((m_State & LogSuppressState.Exception) != 0 && isException);

                if (blocked)
                {
                    m_SuppressedSinceNotice++;
                    m_SuppressedTotal++;

                    // 抑制期不全静默：每 SuppressNoticeSec 放行一条心跳，
                    // 让人能把"被限流"和"进程崩了"区分开（票 05）。
                    if (uptimeMs - m_LastNoticeUptimeMs >= m_Config.SuppressNoticeSec * 1000L)
                    {
                        m_LastNoticeUptimeMs = uptimeMs;
                        LogThrottleDecision beat = Decide(false, LogThrottleNotice.Heartbeat);
                        m_SuppressedSinceNotice = 0;
                        return beat;
                    }

                    return Decide(false, LogThrottleNotice.None);
                }

                // ── 放行。两层计数在这里**各推各的**，互不参照。 ──
                // 异常同时推动两层（它确实既是一条异常、又占了那么多字节），
                // 但推动的是两个独立的账本、对着两个独立的阈值、触顶后抑制两个不同的集合。
                m_SessionBytes += size;
                if (isException)
                {
                    m_ExceptionCount++;
                }

                LogSuppressState next = m_State;
                if (m_SessionBytes >= m_Config.SessionLimitBytes)
                {
                    next |= LogSuppressState.Session;
                }

                if (m_ExceptionCount >= m_Config.ExceptionLimitCount)
                {
                    next |= LogSuppressState.Exception;
                }

                // 标记行只在**状态真的变了**的那一刻插一次。
                // 两层在同一条记录上同时触顶 → 一次 None→(Exception|Session) 的跃迁 → **一行**标记，
                // 不是两行；而先后触顶是两次真实的跃迁，各插一行才说得清"现在连正常日志也停了"。
                if (next != m_State)
                {
                    m_State = next;
                    m_LastNoticeUptimeMs = uptimeMs;
                    LogThrottleDecision marker = Decide(true, LogThrottleNotice.Marker);
                    m_SuppressedSinceNotice = 0;
                    return marker;
                }

                return Decide(true, LogThrottleNotice.None);
            }
        }

        /// <summary>触顶的三种等级（与 <see cref="LogVolume"/> 写堆栈的那一组是同一个集合）。</summary>
        private static bool IsException(LogType type)
        {
            return type == LogType.Error || type == LogType.Assert || type == LogType.Exception;
        }

        /// <summary>把当前字段快照成一个决定。调用方已持锁。</summary>
        private LogThrottleDecision Decide(bool allow, LogThrottleNotice notice)
        {
            return new LogThrottleDecision
            {
                Allow = allow,
                Notice = notice,
                State = m_State,
                SuppressedSinceNotice = m_SuppressedSinceNotice,
                SuppressedTotal = m_SuppressedTotal,
            };
        }
    }
}
