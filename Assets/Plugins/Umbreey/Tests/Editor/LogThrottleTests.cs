using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Umbreey.Tests
{
    /// <summary>
    /// 限流与背压的 EditMode 测试（票 05 / specs 0001「采集」的限流段与「与限流的冲突」段）。
    ///
    /// <para>
    /// specs/0001 的「Testing Decisions」把限流状态机列为第二值得测的接缝：
    /// 输入是一串 <c>(等级, 字节数, 时刻)</c>，输出是"哪些放行、哪些抑制、何时插标记"。
    /// <see cref="LogThrottle"/> **自己不读时钟**，时刻由 <see cref="LogThrottle.Admit"/> 注入 ——
    /// 正因如此，下面那几条与 <see cref="LogConfig.SuppressNoticeSec"/> 有关的断言才能确定性地跑，
    /// 而不是靠真的睡 60 秒（那种测试既慢又必然变成偶发失败）。
    /// </para>
    /// <para>
    /// 这里**不测**采集回调的线程时序、也不测 Debug.Log 有没有真的被调用 ——
    /// 那些是时间与引擎相关的，脆弱且价值低。测的是状态机的决定本身。
    /// </para>
    /// </summary>
    public class LogThrottleTests
    {
        /// <summary>一条普通记录的字节数。取多少无所谓，只要各处一致。</summary>
        private const int RecordSize = 200;

        /// <summary>
        /// 只覆盖本用例关心的那几个阈值，其余留参数总表的默认值。
        /// <para>
        /// ⚠️ 断言里**不写 60 这个字面量**，一律从 <see cref="LogConfig.SuppressNoticeSec"/> 算 ——
        /// 参数总表是唯一事实源，测试把数值再抄一遍就等于又开了一个会漂移的副本。
        /// </para>
        /// </summary>
        private static LogConfig Cfg(int exceptionLimit, long sessionLimit)
        {
            var cfg = new LogConfig();
            cfg.ExceptionLimitCount = exceptionLimit;
            cfg.SessionLimitBytes = sessionLimit;
            return cfg;
        }

        /// <summary>心跳间隔，毫秒。</summary>
        private static long NoticeMs(LogConfig cfg)
        {
            return cfg.SuppressNoticeSec * 1000L;
        }

        /// <summary>业务侧的普通日志。</summary>
        private static LogThrottleDecision Normal(
            LogThrottle throttle, LogType type, long uptimeMs, int size = RecordSize)
        {
            return throttle.Admit(type, size, false, uptimeMs);
        }

        // ================================================================================
        //  异常限流 —— 真正的实时保护（每帧抛异常十几秒就触顶）
        // ================================================================================

        /// <summary>
        /// 票 05 验收 1：异常打满 <see cref="LogConfig.ExceptionLimitCount"/> 条后，
        /// 第 1001 条异常被抑制。
        /// <para>
        /// 边界写死在"第 1000 条仍然放行、第 1001 条不放行"上：判定与记账的先后一旦写反
        /// （先记账再判定），就会变成第 1000 条就被自己挡掉，少收一条最该收的日志。
        /// </para>
        /// </summary>
        [Test]
        public void Admit_PassesExactlyTheExceptionCapAndSuppressesTheNextOne()
        {
            LogConfig cfg = Cfg(1000, long.MaxValue);
            var throttle = new LogThrottle(cfg);

            for (int i = 1; i <= cfg.ExceptionLimitCount; i++)
            {
                LogThrottleDecision d = Normal(throttle, LogType.Exception, 0L);
                Assert.IsTrue(d.Allow, "第 " + i + " 条异常还在配额内，必须放行");
            }

            LogThrottleDecision over = Normal(throttle, LogType.Exception, 0L);

            Assert.IsFalse(over.Allow, "第 " + (cfg.ExceptionLimitCount + 1) + " 条异常必须被抑制");
            Assert.AreEqual(LogSuppressState.Exception, over.State);
        }

        /// <summary>
        /// 票 05 验收 2：异常触顶后，**同一时刻**的 Log / Warning 仍然放行。
        /// <para>
        /// 这是整张票的核心。一个 Update 里的空引用如果连正常日志一起停掉，
        /// 这次会话后面所有业务日志都没了 —— 而排查崩溃时最需要的往往正是异常前后的业务日志，
        /// 不是那一万条一模一样的堆栈。
        /// </para>
        /// </summary>
        [Test]
        public void Admit_KeepsPassingLogAndWarningWhileExceptionsAreSuppressed()
        {
            LogConfig cfg = Cfg(1, long.MaxValue);
            var throttle = new LogThrottle(cfg);

            Normal(throttle, LogType.Exception, 0L);
            Assert.IsFalse(Normal(throttle, LogType.Exception, 0L).Allow, "异常已经触顶");

            Assert.IsTrue(Normal(throttle, LogType.Log, 0L).Allow, "Info 不受异常闸影响");
            Assert.IsTrue(Normal(throttle, LogType.Warning, 0L).Allow, "Warning 不受异常闸影响");
            Assert.IsFalse(Normal(throttle, LogType.Error, 0L).Allow);
            Assert.IsFalse(Normal(throttle, LogType.Assert, 0L).Allow);
        }

        /// <summary>
        /// 异常闸只被 Error / Assert / Exception 推动 —— Log / Warning 打再多也不会让它前进一格。
        /// <para>
        /// 这是"两层独立"在异常这一侧的直接证据：异常计数是**条数**语义，与字节无关。
        /// </para>
        /// </summary>
        [Test]
        public void Admit_OnlyFailureLevelsAdvanceTheExceptionCounter()
        {
            LogConfig cfg = Cfg(1000, long.MaxValue);
            var throttle = new LogThrottle(cfg);

            for (int i = 0; i < 500; i++)
            {
                Normal(throttle, LogType.Log, 0L);
                Normal(throttle, LogType.Warning, 0L);
            }

            Assert.AreEqual(0, throttle.ExceptionCount, "普通日志不该推动异常计数");

            Normal(throttle, LogType.Error, 0L);
            Normal(throttle, LogType.Assert, 0L);
            Normal(throttle, LogType.Exception, 0L);

            Assert.AreEqual(3, throttle.ExceptionCount, "三种失败等级都算异常");
        }

        // ================================================================================
        //  总量限流 —— 磁盘兜底（每帧一条 Info 要 37 分钟才触顶）
        // ================================================================================

        /// <summary>
        /// 票 05 验收 3：总量打满 <see cref="LogConfig.SessionLimitBytes"/> 后，**任意等级**都被抑制。
        /// <para>按字节累计，不是按条数 —— 一条爆栈异常顶得上几千条 Info。</para>
        /// </summary>
        [Test]
        public void Admit_SuppressesEveryLevelOnceTheSessionByteCapIsReached()
        {
            LogConfig cfg = Cfg(int.MaxValue, RecordSize * 3);
            var throttle = new LogThrottle(cfg);

            for (int i = 0; i < 3; i++)
            {
                Assert.IsTrue(Normal(throttle, LogType.Log, 0L).Allow);
            }

            Assert.IsFalse(Normal(throttle, LogType.Log, 0L).Allow);
            Assert.IsFalse(Normal(throttle, LogType.Warning, 0L).Allow);
            Assert.IsFalse(Normal(throttle, LogType.Error, 0L).Allow);
            Assert.IsFalse(Normal(throttle, LogType.Assert, 0L).Allow);
            Assert.IsFalse(Normal(throttle, LogType.Exception, 0L).Allow);
        }

        /// <summary>
        /// 总量按**字节**而不是条数：一条大记录就能顶掉很多条小记录的配额。
        /// <para>写成按条数计的话，一场爆栈刷屏会在总量闸完全无感的情况下把磁盘写满。</para>
        /// </summary>
        [Test]
        public void Admit_CountsTheSessionCapInBytesNotInRecords()
        {
            LogConfig cfg = Cfg(int.MaxValue, 1000);
            var throttle = new LogThrottle(cfg);

            Assert.IsTrue(Normal(throttle, LogType.Log, 0L, 999).Allow);
            Assert.IsTrue(Normal(throttle, LogType.Log, 0L, 1).Allow, "正好走到上限的那条仍然放行");
            Assert.IsFalse(Normal(throttle, LogType.Log, 0L, 1).Allow, "只用了两条记录就触顶");
            Assert.AreEqual(1000L, throttle.SessionBytes);
        }

        // ================================================================================
        //  两层独立 —— 本票最怕写反的地方
        // ================================================================================

        /// <summary>
        /// 票 05 验收 5：异常触顶但总量未触顶时，状态标记必须是「异常触顶」，**不能报成「总量触顶」**。
        /// <para>
        /// 报错了会把排查方向从"谁在每帧抛异常"直接带到"谁在写大日志"，
        /// 而这两件事的现场完全不同 —— 一个看堆栈，一个看日志体积分布。
        /// </para>
        /// </summary>
        [Test]
        public void Admit_ReportsTheExceptionCapAloneWhenTheSessionCapIsUntouched()
        {
            LogConfig cfg = Cfg(1, long.MaxValue);
            var throttle = new LogThrottle(cfg);

            LogThrottleDecision marker = Normal(throttle, LogType.Exception, 0L);

            Assert.AreEqual(LogThrottleNotice.Marker, marker.Notice);
            Assert.AreEqual(LogSuppressState.Exception, marker.State, "不能带上 Session 位");
            Assert.AreEqual(LogSuppressState.None, marker.State & LogSuppressState.Session);
        }

        /// <summary>
        /// 异常闸触顶之后，总量闸**照常独立推进**：Info 继续放行、继续累加字节，
        /// 直到总量自己也触顶才全停。
        /// <para>
        /// 这条是"两层不能合并"的完整证据链：合并成一个配额的话，
        /// 异常刷屏吃光配额的那一刻，下面这些 Info 一条都不会放行。
        /// </para>
        /// </summary>
        [Test]
        public void Admit_KeepsTheTwoCountersIndependentAfterTheExceptionCapIsReached()
        {
            LogConfig cfg = Cfg(1, RecordSize * 10);
            var throttle = new LogThrottle(cfg);

            Normal(throttle, LogType.Exception, 0L);
            long bytesAfterException = throttle.SessionBytes;

            // 异常已经全被挡住，但总量闸眼里这些 Info 是完全正常的流量。
            for (int i = 0; i < 5; i++)
            {
                Assert.IsTrue(Normal(throttle, LogType.Log, 0L).Allow, "第 " + i + " 条 Info 必须放行");
            }

            Assert.AreEqual(LogSuppressState.Exception, throttle.State, "此刻只有异常这一层触顶");
            Assert.AreEqual(bytesAfterException + RecordSize * 5, throttle.SessionBytes);
            Assert.AreEqual(1, throttle.ExceptionCount, "被抑制的异常不该继续推动异常计数");
        }

        /// <summary>
        /// 被抑制的记录**不消耗任何配额** —— 判定在记账之前。
        /// <para>
        /// 反过来写（先记账再判定）会让被抑制的异常继续推着总量往前跑，
        /// 于是"只停异常"会在几十秒后自己退化成"全停"，正是本票要避免的失效模式。
        /// </para>
        /// </summary>
        [Test]
        public void Admit_DoesNotChargeSuppressedRecordsAgainstEitherCounter()
        {
            LogConfig cfg = Cfg(1, long.MaxValue);
            var throttle = new LogThrottle(cfg);

            Normal(throttle, LogType.Exception, 0L);
            long bytes = throttle.SessionBytes;
            int exceptions = throttle.ExceptionCount;

            for (int i = 0; i < 100; i++)
            {
                Assert.IsFalse(Normal(throttle, LogType.Exception, 0L).Allow);
            }

            Assert.AreEqual(bytes, throttle.SessionBytes, "被抑制的记录不该推动总量");
            Assert.AreEqual(exceptions, throttle.ExceptionCount, "被抑制的记录不该推动异常计数");
            Assert.AreEqual(100L, throttle.SuppressedTotal);
        }

        // ================================================================================
        //  标记行
        // ================================================================================

        /// <summary>
        /// 票 05 验收 6：两层在**同一条记录上**同时触顶时，只插一行标记，不是两行。
        /// </summary>
        [Test]
        public void Admit_InsertsASingleMarkerWhenBothCapsAreReachedByTheSameRecord()
        {
            LogConfig cfg = Cfg(1, RecordSize);
            var throttle = new LogThrottle(cfg);

            LogThrottleDecision marker = Normal(throttle, LogType.Exception, 0L);

            Assert.AreEqual(LogThrottleNotice.Marker, marker.Notice);
            Assert.AreEqual(LogSuppressState.Exception | LogSuppressState.Session, marker.State);

            LogThrottleDecision next = Normal(throttle, LogType.Log, 0L);
            Assert.AreEqual(LogThrottleNotice.None, next.Notice, "同一个状态不再插第二行标记");
        }

        /// <summary>
        /// 同一个抑制状态下不会反复插标记 —— 否则触顶的那一秒会刷出成百上千行标记，
        /// 把它自己想要标记的那件事淹掉。
        /// </summary>
        [Test]
        public void Admit_InsertsTheMarkerOnlyOncePerState()
        {
            LogConfig cfg = Cfg(1, long.MaxValue);
            var throttle = new LogThrottle(cfg);

            Assert.AreEqual(LogThrottleNotice.Marker, Normal(throttle, LogType.Exception, 0L).Notice);

            for (int i = 0; i < 50; i++)
            {
                Assert.AreEqual(
                    LogThrottleNotice.None, Normal(throttle, LogType.Exception, 0L).Notice,
                    "触顶之后不该再插标记");
            }
        }

        /// <summary>
        /// 先后触顶是**两次真实的状态跃迁**，各插一行标记。
        /// <para>
        /// 这与上一条不矛盾：第二行说的是一件新事情 —— "现在连正常日志也停了"。
        /// 不插的话，CLS 里只留着一行"只停异常"，而实际上 Info 也早就没了。
        /// </para>
        /// </summary>
        [Test]
        public void Admit_InsertsASecondMarkerWhenTheOtherCapIsReachedLater()
        {
            LogConfig cfg = Cfg(1, RecordSize * 4);
            var throttle = new LogThrottle(cfg);

            LogThrottleDecision first = Normal(throttle, LogType.Exception, 0L);
            Assert.AreEqual(LogThrottleNotice.Marker, first.Notice);
            Assert.AreEqual(LogSuppressState.Exception, first.State);

            Normal(throttle, LogType.Log, 0L);
            Normal(throttle, LogType.Log, 0L);
            LogThrottleDecision second = Normal(throttle, LogType.Log, 0L);

            Assert.AreEqual(LogThrottleNotice.Marker, second.Notice, "总量后来也触顶，这是新的一件事");
            Assert.AreEqual(LogSuppressState.Exception | LogSuppressState.Session, second.State);
        }

        // ================================================================================
        //  心跳 —— 触顶后不全静默
        // ================================================================================

        /// <summary>
        /// 票 05 验收 4：抑制状态下，第 60 秒放行一条心跳、第 61~119 秒不放行、第 120 秒再放行一条。
        /// <para>
        /// 全静默会让人在 CLS 里看到"日志突然断了"，却分不清是进程崩了还是被限流了 ——
        /// 这两种情况的排查方向完全相反。
        /// </para>
        /// <para>时刻全部由参数注入，因此这条断言是确定性的，不依赖真实时间流逝。</para>
        /// </summary>
        [Test]
        public void Admit_EmitsOneHeartbeatPerSuppressNoticeWindow()
        {
            LogConfig cfg = Cfg(1, long.MaxValue);
            long window = NoticeMs(cfg);
            var throttle = new LogThrottle(cfg);

            // t=0 触顶并插标记，心跳的计时从这一刻开始。
            Assert.AreEqual(LogThrottleNotice.Marker, Normal(throttle, LogType.Exception, 0L).Notice);

            // 第 1~59 秒：一条都不放行。
            for (long t = 1000L; t < window; t += 1000L)
            {
                Assert.AreEqual(
                    LogThrottleNotice.None, Normal(throttle, LogType.Exception, t).Notice,
                    "第 " + (t / 1000L) + " 秒还没到窗口");
            }

            // 第 60 秒：放行一条。
            Assert.AreEqual(
                LogThrottleNotice.Heartbeat, Normal(throttle, LogType.Exception, window).Notice,
                "第 " + cfg.SuppressNoticeSec + " 秒必须有一条心跳");

            // 第 61~119 秒：不放行。
            for (long t = window + 1000L; t < window * 2L; t += 1000L)
            {
                Assert.AreEqual(
                    LogThrottleNotice.None, Normal(throttle, LogType.Exception, t).Notice,
                    "第 " + (t / 1000L) + " 秒在上一条心跳的窗口内");
            }

            // 第 120 秒：再放行一条。
            Assert.AreEqual(
                LogThrottleNotice.Heartbeat, Normal(throttle, LogType.Exception, window * 2L).Notice,
                "第 " + (cfg.SuppressNoticeSec * 2) + " 秒必须有第二条心跳");
        }

        /// <summary>
        /// 心跳自己是**不放行**的：它是一条新造的 Internal 通告，不是把被抑制的那条业务日志放出去。
        /// <para>放出去的话，触顶后每 60 秒仍会漏一条原始记录进卷，而且那条记录说不出限流状态。</para>
        /// </summary>
        [Test]
        public void Admit_KeepsSuppressingTheRecordThatTriggersTheHeartbeat()
        {
            LogConfig cfg = Cfg(1, long.MaxValue);
            var throttle = new LogThrottle(cfg);

            Normal(throttle, LogType.Exception, 0L);
            LogThrottleDecision beat = Normal(throttle, LogType.Exception, NoticeMs(cfg));

            Assert.AreEqual(LogThrottleNotice.Heartbeat, beat.Notice);
            Assert.IsFalse(beat.Allow, "触发心跳的那条业务记录本身仍然被抑制");
        }

        /// <summary>
        /// 票 05 验收：心跳那一条要能看出**当前处于哪种抑制状态**、**已经抑制了多少条**。
        /// <para>
        /// 少了状态，就分不清是"只停异常"还是"全停"；少了条数，就不知道被吞掉的是 10 条还是 10 万条 ——
        /// 后者直接决定要不要回头查失控源。
        /// </para>
        /// </summary>
        [Test]
        public void Heartbeat_CarriesTheSuppressStateAndTheSuppressedCount()
        {
            LogConfig cfg = Cfg(1, long.MaxValue);
            long window = NoticeMs(cfg);
            var throttle = new LogThrottle(cfg);

            Normal(throttle, LogType.Exception, 0L);

            // 窗口内被吞掉 9 条，第 10 条触发心跳 —— 心跳要把这 10 条都算进去。
            for (int i = 0; i < 9; i++)
            {
                Normal(throttle, LogType.Exception, 1000L);
            }

            LogThrottleDecision beat = Normal(throttle, LogType.Exception, window);

            Assert.AreEqual(LogSuppressState.Exception, beat.State, "状态要能区分是哪一层");
            Assert.AreEqual(10, beat.SuppressedSinceNotice, "含触发心跳的这一条");
            Assert.AreEqual(10L, beat.SuppressedTotal);
        }

        /// <summary>
        /// "自上次通告以来"的计数在每条心跳后清零，累计数继续涨。
        /// <para>不清零的话第二条心跳会把第一条已经报过的量再报一遍，读的人会以为丢失量在翻倍。</para>
        /// </summary>
        [Test]
        public void Heartbeat_ResetsThePerWindowCountButKeepsTheSessionTotal()
        {
            LogConfig cfg = Cfg(1, long.MaxValue);
            long window = NoticeMs(cfg);
            var throttle = new LogThrottle(cfg);

            Normal(throttle, LogType.Exception, 0L);
            Normal(throttle, LogType.Exception, 1000L);
            LogThrottleDecision first = Normal(throttle, LogType.Exception, window);

            Normal(throttle, LogType.Exception, window + 1000L);
            LogThrottleDecision second = Normal(throttle, LogType.Exception, window * 2L);

            Assert.AreEqual(2, first.SuppressedSinceNotice);
            Assert.AreEqual(2, second.SuppressedSinceNotice, "第二个窗口只报第二个窗口的量");
            Assert.AreEqual(4L, second.SuppressedTotal, "累计数横跨所有窗口");
        }

        /// <summary>
        /// 没有进入抑制状态时永远不会有心跳 —— 正常运行的会话里不该出现这行噪音。
        /// </summary>
        [Test]
        public void Admit_NeverEmitsAHeartbeatWhileNothingIsSuppressed()
        {
            LogConfig cfg = Cfg(int.MaxValue, long.MaxValue);
            var throttle = new LogThrottle(cfg);

            for (long t = 0L; t <= NoticeMs(cfg) * 5L; t += NoticeMs(cfg))
            {
                LogThrottleDecision d = Normal(throttle, LogType.Log, t);
                Assert.IsTrue(d.Allow);
                Assert.AreEqual(LogThrottleNotice.None, d.Notice);
            }
        }

        // ================================================================================
        //  Internal 日志 bypass —— 否则触顶后连"我被限流了"都发不出来
        // ================================================================================

        /// <summary>
        /// 全局硬约束 6：Internal 日志**整条 bypass** —— 两层都触顶了它照样放行。
        /// <para>
        /// 标记行和心跳自己就是 Internal 日志。它们要是也被抑制，
        /// 触顶之后系统就再也说不出一个字，而那正是最需要它说话的时候。
        /// </para>
        /// </summary>
        [Test]
        public void Admit_AlwaysPassesInternalRecordsEvenWhenBothCapsAreReached()
        {
            LogConfig cfg = Cfg(1, RecordSize);
            var throttle = new LogThrottle(cfg);

            Normal(throttle, LogType.Exception, 0L);
            Assert.AreEqual(LogSuppressState.Exception | LogSuppressState.Session, throttle.State);

            Assert.IsTrue(throttle.Admit(LogType.Log, RecordSize, true, 0L).Allow);
            Assert.IsTrue(throttle.Admit(LogType.Warning, RecordSize, true, 0L).Allow);
            Assert.IsTrue(throttle.Admit(LogType.Error, RecordSize, true, 0L).Allow);
            Assert.IsTrue(throttle.Admit(LogType.Exception, RecordSize, true, 0L).Allow);
        }

        /// <summary>
        /// Internal 日志**不计入任何一层计数**：既不推异常条数，也不推会话字节。
        /// <para>
        /// 计进去的话就成了自指的死循环：限流通告本身把配额耗光，
        /// 于是产生更多通告 —— 而通告的产生频率恰恰与抑制量成正比。
        /// </para>
        /// </summary>
        [Test]
        public void Admit_DoesNotCountInternalRecordsAgainstEitherCap()
        {
            LogConfig cfg = Cfg(1000, long.MaxValue);
            var throttle = new LogThrottle(cfg);

            for (int i = 0; i < 2000; i++)
            {
                throttle.Admit(LogType.Error, RecordSize, true, 0L);
            }

            Assert.AreEqual(0, throttle.ExceptionCount, "Internal 不推异常计数");
            Assert.AreEqual(0L, throttle.SessionBytes, "Internal 不推会话字节");
            Assert.AreEqual(LogSuppressState.None, throttle.State, "两千条 Internal 也不该触顶");
        }

        /// <summary>
        /// Internal 日志不会触发标记或心跳 —— 递归到此为止。
        /// <para>
        /// 心跳走 Debug.Log 会同步回到采集回调，那一趟带着 Internal = true。
        /// 如果它自己又能触发一条心跳，就是一个无限递归（全局硬约束 6 的重入守卫讲的是同一件事，
        /// 这里是状态机侧的第二道保险）。
        /// </para>
        /// </summary>
        [Test]
        public void Admit_NeverLetsAnInternalRecordTriggerAnotherNotice()
        {
            LogConfig cfg = Cfg(1, long.MaxValue);
            var throttle = new LogThrottle(cfg);

            Normal(throttle, LogType.Exception, 0L);

            // 时刻远远越过心跳窗口：普通记录到这里必然触发心跳，Internal 记录必须什么都不触发。
            LogThrottleDecision internalDecision =
                throttle.Admit(LogType.Error, RecordSize, true, NoticeMs(cfg) * 10L);

            Assert.IsTrue(internalDecision.Allow);
            Assert.AreEqual(LogThrottleNotice.None, internalDecision.Notice);
        }

        /// <summary>
        /// 心跳与标记行**仍然进 outbox**（specs/0001「与限流的冲突」）——
        /// Internal 日志只是 bypass 限流，不是不上传。
        /// <para>
        /// <see cref="LogSystem.AppendUploadable"/> 只拦 <see cref="LogRecord.Sensitive"/>，
        /// 这一条把"Internal 没有被顺手一起拦掉"钉住。
        /// </para>
        /// </summary>
        [Test]
        public void AppendUploadable_KeepsInternalRecordsSoNoticesStillReachTheOutbox()
        {
            var source = new List<LogRecord>
            {
                LogRecord.Capture(0L, LogType.Log, "[LogSystem] throttle engaged", "", 0, true),
            };
            var batch = new List<LogRecord>();

            int bytes = LogSystem.AppendUploadable(source, batch);

            Assert.AreEqual(1, batch.Count, "限流通告必须进批次累加器");
            Assert.IsTrue(batch[0].Internal);
            Assert.AreEqual(source[0].Size, bytes);
        }

        // ================================================================================
        //  卷满之后心跳往哪写（specs/0001「与限流的冲突：卷满之后心跳往哪写」）
        // ================================================================================

        /// <summary>
        /// **卷满属于硬停，心跳也不再写卷。** Internal 日志 bypass 的是限流计数，
        /// **不 bypass 卷满** —— 两者是不同的闸，不能混为一谈。
        /// <para>
        /// 写反的后果是卷在 <see cref="LogVolume.FullNotice"/> 之后又开始长：
        /// "一个卷 == 一次完整会话且不超过 VolumeMaxBytes"这条保证就破了，
        /// 而它正是"发一个卷 == 发完整一次运行"的前提。
        /// </para>
        /// <para>心跳仍然进 outbox（上传侧还有自己的配额），那一侧由上面那条用例钉住。</para>
        /// </summary>
        [Test]
        public void Write_KeepsTheVolumeSilentForInternalNoticesAfterItIsFull()
        {
            string dir = Path.Combine(
                Path.GetTempPath(), "UmbreeyLogThrottleTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                var cfg = new LogConfig();
                cfg.VolumeMaxBytes = 64;

                var volume = new LogVolume(cfg, dir, "Niniko GO", "a1b2c3d4-1111-2222-3333-444455556666");
                volume.Open();

                volume.Write(new List<LogRecord> { LogRecord.Capture(0L, LogType.Log, new string('a', 100), "", 0, false) });
                Assert.IsTrue(volume.IsFull, "先把卷写满");

                // 一条 Internal 的限流心跳。它在限流那一侧是畅通无阻的，但卷已经硬停了。
                volume.Write(new List<LogRecord>
                {
                    LogRecord.Capture(0L, LogType.Log, "[LogSystem] throttle still active: heartbeat", "", 0, true),
                });
                volume.Close();

                string text = File.ReadAllText(volume.FilePath);

                StringAssert.Contains(LogVolume.FullNotice, text);
                Assert.IsFalse(
                    text.Contains("throttle still active"),
                    "卷满之后连限流心跳也不写卷 —— 心跳 bypass 的是限流，不是卷满");
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                }
            }
        }

        // ================================================================================
        //  背压（票 03 实现，票 05 的验收项再钉一次）
        // ================================================================================

        /// <summary>
        /// 票 05 验收 7：连续塞入超过上限的记录时，丢的是**最早塞进去的那些**，计数准确。
        ///
        /// <para>
        /// 丢最旧不是笔误：队列是 FIFO，最靠近崩溃的是**最新**的记录，保留最新就意味着丢最旧。
        /// 这与限流的方向刚好相反（限流丢的是最新的，因为配额已经用完了）——
        /// 两道闸的立意不同，背压在保现场，限流在保配额。
        /// </para>
        /// <para>
        /// ⚠️ 在缓冲里被丢的记录**还没写过本地卷**，本地卷和 outbox 会同时失去它（ADR-0001）。
        /// 不要在任何注释或日志文案里宣称"被丢的记录还在本地卷里"。
        /// </para>
        /// </summary>
        [Test]
        public void Buffer_DropsTheOldestRecordsAndReportsAnAccurateCount()
        {
            var cfg = new LogConfig();
            cfg.BufferLimitBytes = RecordSize * 5;
            var buffer = new LogBuffer(cfg);

            for (int i = 0; i < 12; i++)
            {
                buffer.Add(new LogRecord
                {
                    TsUnixMs = 0L,
                    Type = LogType.Log,
                    Msg = i.ToString(),
                    Stack = "",
                    Size = RecordSize,
                    CtxVer = 0,
                    Internal = false,
                    Sensitive = false,
                });
            }

            int dropped;
            IReadOnlyList<LogRecord> swapped = buffer.Swap(out dropped);

            Assert.AreEqual(7, dropped, "12 条塞进只装得下 5 条的缓冲，丢弃计数必须是 7");
            Assert.AreEqual(5, swapped.Count);
            Assert.AreEqual("7", swapped[0].Msg, "留下的是最新的 5 条");
            Assert.AreEqual("11", swapped[swapped.Count - 1].Msg, "最后一条 —— 最靠近崩溃的那条 —— 必须还在");
        }
    }
}
