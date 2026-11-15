using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Umbreey.Tests
{
    /// <summary>
    /// 生命周期 flush 的 EditMode 测试（票 06 / specs 0001「启动与生命周期」/ ADR-0001 末段）。
    ///
    /// <para>
    /// 可测的接缝有两个，都被刻意做成了"参数注入、内部不读时钟、不碰 IO、不碰 Unity API"的形状
    /// （和票 05 的 <see cref="LogThrottle"/> 一样）：
    /// </para>
    /// <para>
    /// · <see cref="LogFlushGate"/> —— 序号水位握手。序号与超时预算都是参数，
    ///   因此"迟到的确认""写盘失败也算结束"这些行为都能确定性地摆出来，
    ///   不必真的去写一个坏磁盘、也不必 sleep 到超时。
    /// </para>
    /// <para>
    /// · <see cref="LogStopSequence"/> —— 有界停止顺序。顺序错了不会有编译错误、也不会抛异常，
    ///   只会在真机上表现成"退出时最后一段日志没了"或"退出卡 10 秒"，
    ///   所以它必须被一条断言钉死。
    /// </para>
    /// <para>
    /// 这里**不测打包线程的唤醒时序**（时间相关，脆弱且价值低，specs/0001「Testing Decisions」）。
    /// 少数几处对"耗时"的断言都留了一个数量级以上的余量，断的是"没有等满超时"这件事本身。
    /// </para>
    /// </summary>
    public class LogFlushTests
    {
        /// <summary>
        /// 水位满足时的等待预算。用一个大值，这样"提前返回"和"等满超时"在耗时上差一个数量级，
        /// 断言不必卡在毫秒上。
        /// </summary>
        private const int GenerousWaitMs = 5000;

        /// <summary>预期会超时的用例用它。短，但足够跨过一次线程调度。</summary>
        private const int ShortWaitMs = 30;

        /// <summary>"提前返回"的判定线。远小于 <see cref="GenerousWaitMs"/>，不会偶发。</summary>
        private const int PromptMs = 1500;

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
        }

        private static LogRecord Rec(int size = 100)
        {
            return new LogRecord
            {
                TsUnixMs = 0L,
                Type = LogType.Log,
                Msg = "m",
                Stack = "",
                Size = size,
                CtxVer = 0,
                Internal = false,
                Sensitive = false,
            };
        }

        private static LogBuffer NewBuffer(int limitBytes = 1024 * 1024)
        {
            var cfg = new LogConfig();
            cfg.BufferLimitBytes = limitBytes;
            return new LogBuffer(cfg);
        }

        // ================================================================================
        //  序号水位：满足就返回，不多等
        // ================================================================================

        /// <summary>
        /// 水位已经到位时立刻返回，**不多等一毫秒**。
        /// <para>
        /// flush 卡的是主线程，虽然卡在玩家感知不到的时刻（切后台/退出的瞬间），
        /// 但"没事也睡满 300ms"会让每次切后台都白付这个代价，而 pause 的预算是要留给
        /// 真的在写盘的那些情况的。
        /// </para>
        /// </summary>
        [Test]
        public void Wait_ReturnsAsSoonAsTheWatermarkIsReached()
        {
            var gate = new LogFlushGate();
            gate.ReportRound(10L, true, 0L);

            var elapsed = Stopwatch.StartNew();
            LogFlushResult result = gate.Wait(10L, 0L, GenerousWaitMs);
            elapsed.Stop();

            Assert.IsTrue(result.Completed);
            Assert.AreEqual(0L, result.Pending);
            Assert.Less(
                elapsed.ElapsedMilliseconds, PromptMs,
                "水位已经到位还等下去，等于每次切后台都白付一个 FlushWaitMs");
        }

        /// <summary>
        /// 打包线程还没报到目标水位时，等待会超时，并**如实说出还差多少条**。
        /// <para>这条数字是降级路径的全部信息量：未写完的记录留在缓冲里，进程被杀就没了。</para>
        /// </summary>
        [Test]
        public void Wait_TimesOutAndReportsHowManyRecordsAreStillPending()
        {
            var gate = new LogFlushGate();
            gate.ReportRound(3L, true, 0L);

            LogFlushResult result = gate.Wait(10L, 0L, ShortWaitMs);

            Assert.IsFalse(result.Completed);
            Assert.IsFalse(result.Closed, "这是超时，不是停机 —— 两者的诊断文案必须分得开");
            Assert.AreEqual(3L, result.Written);
            Assert.AreEqual(7L, result.Pending);
        }

        /// <summary>
        /// **本票最容易做错的一条**：上一轮超时之后迟到的确认，不能误满足下一轮 Flush。
        ///
        /// <para>
        /// 用布尔信号（<c>AutoResetEvent</c> / <c>ManualResetEvent</c>）就会错：
        /// 第一轮超时放弃了，打包线程随后才把信号置位 —— 这个信号没人消费，一直留在那儿。
        /// 第二轮 flush 一进来就被它满足，于是**明明还有 5 条记录躺在缓冲里，Flush 却报成功**。
        /// 而调用它的正是 pause，紧接着进程就被系统杀掉了：日志真的丢了，还没有任何报警。
        /// </para>
        /// <para>
        /// 水位不会：迟到的那次确认带着的是**旧的序号**，它把水位推到旧的高度，
        /// 而第二轮的目标是当时重新读的、更高的号。
        /// </para>
        /// </summary>
        [Test]
        public void ALateConfirmationFromATimedOutFlushDoesNotSatisfyTheNextFlush()
        {
            LogBuffer buffer = NewBuffer();
            var gate = new LogFlushGate();

            // ── 第一轮：10 条记录进了缓冲，flush 等它们落盘，磁盘忙，超时放弃。 ──
            for (int i = 0; i < 10; i++)
            {
                buffer.Add(Rec());
            }

            long firstTarget = buffer.EnqueuedSeq;
            LogFlushResult first = gate.Wait(firstTarget, 0L, ShortWaitMs);
            Assert.IsFalse(first.Completed, "前提：第一轮必须是超时的，否则这条用例什么也没测");

            // ── 迟到的确认：打包线程忙完了，报了第一轮那批的水位。没人在等它了。 ──
            gate.ReportRound(firstTarget, true, 0L);

            // ── 第二轮：又来了 5 条新记录，pause 触发 flush。 ──
            for (int i = 0; i < 5; i++)
            {
                buffer.Add(Rec());
            }

            long secondTarget = buffer.EnqueuedSeq;
            LogFlushResult second = gate.Wait(secondTarget, 0L, ShortWaitMs);

            Assert.IsFalse(
                second.Completed,
                "迟到的确认满足了下一轮 flush —— 这正是布尔信号会犯、水位不该犯的错");
            Assert.AreEqual(firstTarget, second.Written);
            Assert.AreEqual(5L, second.Pending, "差的正是第二轮那 5 条");

            // 对照：真的把新的水位报上来，这一轮才算数。
            gate.ReportRound(secondTarget, true, 0L);
            Assert.IsTrue(gate.Wait(secondTarget, 0L, GenerousWaitMs).Completed);
        }

        /// <summary>
        /// 被唤醒本身不算数：报一个**更低**的序号会 Pulse 到等待方，但谓词没满足，它得接着睡。
        /// <para>
        /// 这是"迟到的确认不误满足"的第二道保证 —— <c>Monitor.Wait</c> 外面套的是
        /// **循环复检谓词**，不是"醒了就返回"。少了这个循环，一次无关的唤醒就能骗过 flush。
        /// </para>
        /// </summary>
        [Test]
        public void Wait_IsNotSatisfiedByAWakeupAlone()
        {
            var gate = new LogFlushGate();

            // 等待期间不停地报 —— 但报的号永远够不到 target，每次 Pulse 都只是把等待方吵醒。
            var reporter = new Thread(() =>
            {
                for (int i = 1; i <= 5; i++)
                {
                    gate.ReportRound(i, true, 0L);
                    Thread.Sleep(1);
                }
            });

            reporter.IsBackground = true;
            reporter.Start();

            LogFlushResult result = gate.Wait(100L, 0L, ShortWaitMs);
            reporter.Join();

            Assert.IsFalse(result.Completed, "被唤醒不等于水位到位");
            Assert.LessOrEqual(result.Written, 5L, "报进来的号一个都没够到 target");
        }

        /// <summary>
        /// Flush **只保证调用那一刻已经在缓冲里的记录**，之后到达的不在保证范围内。
        /// <para>
        /// 没有这条边界，一个持续打日志的线程能让 flush 永远追不上自己的目标 ——
        /// 那就不是"有界的停止顺序"了。
        /// </para>
        /// </summary>
        [Test]
        public void Flush_OnlyGuaranteesTheRecordsThatWereInTheBufferWhenItWasCalled()
        {
            LogBuffer buffer = NewBuffer();
            var gate = new LogFlushGate();

            for (int i = 0; i < 3; i++)
            {
                buffer.Add(Rec());
            }

            // 目标就在这一刻定死。
            long target = buffer.EnqueuedSeq;

            // 之后又来了两条 —— 它们拿到的是更大的号，不属于本轮。
            buffer.Add(Rec());
            buffer.Add(Rec());

            gate.ReportRound(target, true, 0L);
            LogFlushResult result = gate.Wait(target, 0L, GenerousWaitMs);

            Assert.IsTrue(result.Completed, "本轮该等的三条已经落地，不该被后到的记录拖住");
            Assert.AreEqual(3L, result.Written);
            Assert.AreEqual(5L, buffer.EnqueuedSeq, "后到的记录照常领号，只是不归这一轮管");
        }

        /// <summary>
        /// 水位必须在**交换的那个锁段里**取：Swap 交出的号，正是这一批记录的上界。
        /// <para>
        /// 事后再读 <see cref="LogBuffer.EnqueuedSeq"/> 就错了 —— 两者之间新到的记录会让
        /// 报出去的水位高于实际写出的内容，那就是"假成功"。
        /// </para>
        /// </summary>
        [Test]
        public void Swap_HandsOutTheWatermarkOfExactlyThatBatch()
        {
            LogBuffer buffer = NewBuffer();
            for (int i = 0; i < 3; i++)
            {
                buffer.Add(Rec());
            }

            int dropped;
            long first;
            IReadOnlyList<LogRecord> batch = buffer.Swap(out dropped, out first);
            Assert.AreEqual(3, batch.Count);
            Assert.AreEqual(3L, first);

            buffer.Add(Rec());
            buffer.Add(Rec());

            long second;
            buffer.Swap(out dropped, out second);
            Assert.AreEqual(5L, second, "水位是累计量，不是每批的条数");
        }

        /// <summary>
        /// 背压丢掉的记录**照样占号**，水位不回退。
        /// <para>
        /// 反过来（丢弃时把号退回去）会让 Flush 为几条永远不会落盘的记录一直等到超时 ——
        /// 每次切后台白白多花一个 FlushWaitMs，还会报一条"N 条未落盘"的误导诊断。
        /// 水位准确的语义是"号 ≤ N 的都已**处理完毕**（写盘或被背压丢弃）"。
        /// </para>
        /// </summary>
        [Test]
        public void EnqueuedSeq_CountsRecordsDroppedByBackpressureToo()
        {
            // 上限只装得下 2 条，加 10 条必然丢掉一批。
            LogBuffer buffer = NewBuffer(200);
            for (int i = 0; i < 10; i++)
            {
                buffer.Add(Rec(100));
            }

            int dropped;
            long seq;
            IReadOnlyList<LogRecord> batch = buffer.Swap(out dropped, out seq);

            Assert.Greater(dropped, 0, "前提：这一轮必须真的丢了记录");
            Assert.AreEqual(10L, seq, "被丢的记录也进过缓冲，号不能退回去");
            Assert.AreEqual(10, batch.Count + dropped);
        }

        // ================================================================================
        //  写盘 IO 失败：也算这一轮结束
        // ================================================================================

        /// <summary>
        /// 写盘失败**也算这一轮结束**：水位照常推进，Flush 立刻返回，不等到超时。
        /// <para>
        /// 失败时不推进水位的话，磁盘一坏，每一次 pause 都要额外卡满一个 FlushWaitMs ——
        /// 而磁盘已经坏了，等下去不会有任何好转。
        /// "这一轮结束了"和"这一轮成功了"是两件事：前者决定放不放人走，后者决定要不要留诊断。
        /// </para>
        /// </summary>
        [Test]
        public void AFailedWriteStillEndsTheRoundInsteadOfMakingFlushWaitForTheTimeout()
        {
            var gate = new LogFlushGate();
            gate.ReportRound(10L, false, 0L);

            var elapsed = Stopwatch.StartNew();
            LogFlushResult result = gate.Wait(10L, 0L, GenerousWaitMs);
            elapsed.Stop();

            Assert.IsTrue(result.Completed, "写盘失败也是结束，不能让调用方一直等到超时");
            Assert.IsTrue(result.WriteFailed, "但必须留痕，否则'日志没写进去'会表现成卷里静悄悄少一段");
            Assert.Less(elapsed.ElapsedMilliseconds, PromptMs);
        }

        /// <summary>
        /// 写盘失败要产生一条 <see cref="LogRecord.Internal"/> 诊断 ——
        /// 文案由这个纯函数生成，因此可以直接断言（真正的 <c>Debug.LogError</c> 由
        /// <c>LogSystem.ReportFlush</c> 发出，测试不去触发它）。
        /// <para>正常完成且没出错时**返回 null**：flush 是每次切后台都会发生的事，不能话痨。</para>
        /// </summary>
        [Test]
        public void DescribeFlush_ProducesADiagnosticForAFailedWriteAndStaysSilentOtherwise()
        {
            var failed = new LogFlushResult
            {
                Completed = true,
                Target = 10L,
                Written = 10L,
                WriteFailed = true,
                WaitedForMs = 300,
            };

            string message = LogSystem.DescribeFlush(failed);
            Assert.IsNotNull(message, "写盘失败必须留一条诊断");
            StringAssert.Contains("write failure", message);

            var clean = new LogFlushResult
            {
                Completed = true,
                Target = 10L,
                Written = 10L,
                WriteFailed = false,
                WaitedForMs = 300,
            };

            Assert.IsNull(LogSystem.DescribeFlush(clean), "一切正常时不该有任何输出");
        }

        /// <summary>
        /// 超时的诊断必须说清两件事：**等了多久**、**还有多少条没落盘**。
        /// <para>只说"flush 超时"，排查的人无从判断这次到底丢没丢、丢了多少。</para>
        /// </summary>
        [Test]
        public void DescribeFlush_ReportsTheTimeoutAndTheNumberOfRecordsNotOnDisk()
        {
            var timedOut = new LogFlushResult
            {
                Completed = false,
                Target = 42L,
                Written = 12L,
                WriteFailed = false,
                Closed = false,
                WaitedForMs = 300,
            };

            string message = LogSystem.DescribeFlush(timedOut);

            Assert.IsNotNull(message);
            StringAssert.Contains("timed out after 300ms", message);
            StringAssert.Contains("up to 30 record", message, "还差 42-12=30 条，这个数字是降级路径的全部信息量");
        }

        /// <summary>
        /// 停机中途结束的 flush **不能报成超时** —— 两者的排查方向完全不同：
        /// 一个是磁盘慢，一个是打包线程已经不在了。
        /// </summary>
        [Test]
        public void DescribeFlush_TellsShutdownApartFromTimeout()
        {
            var stopped = new LogFlushResult
            {
                Completed = false,
                Target = 10L,
                Written = 0L,
                Closed = true,
                WaitedForMs = 300,
            };

            string message = LogSystem.DescribeFlush(stopped);

            Assert.IsNotNull(message);
            StringAssert.Contains("stopping", message);
            Assert.IsFalse(message.Contains("timed out"), "停机不是超时");
        }

        /// <summary>
        /// 写盘失败的标记描述的是**刚刚这一轮**，被读走就清零 ——
        /// 否则一次失败会给之后每一次 flush 都糊上一条同样的诊断。
        /// </summary>
        [Test]
        public void TheWriteFailureFlagBelongsToOneRoundOnly()
        {
            var gate = new LogFlushGate();
            gate.ReportRound(1L, false, 0L);

            Assert.IsTrue(gate.Wait(1L, 0L, GenerousWaitMs).WriteFailed);

            gate.ReportRound(2L, true, 0L);
            Assert.IsFalse(gate.Wait(2L, 0L, GenerousWaitMs).WriteFailed, "上一轮的失败不该跟着跑到下一轮");
        }

        // ================================================================================
        //  强制 outbox 批次（pause）：票号，同样不是布尔标志
        // ================================================================================

        /// <summary>
        /// pause 的 flush 要等到"**真的切了一个批出去**"才算完成，光是水位到位不够。
        /// <para>
        /// 切后台的那一刻缓冲可能已经空了（水位天然满足），但内存里还攒着半个批次 ——
        /// 那才是 pause 要固化的东西。只看水位就会在 outbox 批次还没产出时提前返回。
        /// </para>
        /// </summary>
        [Test]
        public void AForcedBatchRequestIsNotSatisfiedUntilTheRoundThatServesItReports()
        {
            var gate = new LogFlushGate();
            gate.ReportRound(10L, true, 0L);

            long ticket = gate.RequestForceBatch();
            Assert.AreEqual(1L, ticket);

            // 水位早就够了，但这一轮没服务强制切批。
            LogFlushResult pending = gate.Wait(10L, ticket, ShortWaitMs);
            Assert.IsFalse(pending.Completed, "outbox 批次还没产出，pause 不能提前返回");

            // 打包线程服务掉这张票。
            gate.ReportRound(10L, true, ticket);
            Assert.IsTrue(gate.Wait(10L, ticket, GenerousWaitMs).Completed);
        }

        /// <summary>
        /// 没有待处理的请求时取到 0；有请求且还没被服务时会**一直**取到它 ——
        /// 中途出异常的那一轮不会把请求吃掉，下一轮接着服务。
        /// </summary>
        [Test]
        public void TakeForceBatch_KeepsHandingOutTheTicketUntilARoundActuallyServesIt()
        {
            var gate = new LogFlushGate();
            Assert.AreEqual(0L, gate.TakeForceBatch());

            long ticket = gate.RequestForceBatch();
            Assert.AreEqual(ticket, gate.TakeForceBatch());
            Assert.AreEqual(ticket, gate.TakeForceBatch(), "还没报完成，请求就还在");

            gate.ReportRound(0L, true, ticket);
            Assert.AreEqual(0L, gate.TakeForceBatch(), "服务过了就不该再切一次");
        }

        // ================================================================================
        //  关闭：幂等，且放走等待方
        // ================================================================================

        /// <summary>
        /// 停机时闸门要立刻放走还卡在 flush 里的调用方，不让退出白白多花一个 FlushWaitMs。
        /// </summary>
        [Test]
        public void Close_ReleasesWaitersImmediatelyAndMarksTheResultAsStopped()
        {
            var gate = new LogFlushGate();
            gate.Close();

            var elapsed = Stopwatch.StartNew();
            LogFlushResult result = gate.Wait(999L, 0L, GenerousWaitMs);
            elapsed.Stop();

            Assert.IsFalse(result.Completed);
            Assert.IsTrue(result.Closed);
            Assert.Less(elapsed.ElapsedMilliseconds, PromptMs, "停机之后不该还在等一个没人服务的水位");
        }

        /// <summary>
        /// **关闭必须幂等**：退出流程与 Editor 域重载走的是同一条路径，
        /// 重复调用一次都不能抛。
        /// </summary>
        [Test]
        public void Close_IsIdempotent()
        {
            var gate = new LogFlushGate();

            Assert.DoesNotThrow(() =>
            {
                gate.Close();
                gate.Close();
                gate.Close();
            });

            Assert.IsTrue(gate.IsClosed);
        }

        /// <summary>
        /// 卷句柄的关闭同样幂等：打包线程在 FinalDrain 里关一次，主线程兜底时可能再关一次。
        /// <para>抢着关也不能抛 —— 泄漏句柄的表现是"下次进播放模式打不开卷"。</para>
        /// </summary>
        [Test]
        public void VolumeClose_IsIdempotent()
        {
            string dir = Path.Combine(Path.GetTempPath(), "UmbreeyLogFlushTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                var volume = new LogVolume(new LogConfig(), dir, "Niniko GO", Guid.NewGuid().ToString("D"));
                volume.Open();

                Assert.DoesNotThrow(() =>
                {
                    volume.Close();
                    volume.Close();
                });

                // 关掉之后再写也不能抛，且要报"这一轮没有写盘失败"——
                // 句柄已关是正常的硬停，不是 IO 故障。
                Assert.IsTrue(volume.Write(new List<LogRecord> { Rec() }));
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
        //  有界的停止顺序
        // ================================================================================

        /// <summary>记录每一步被调用的顺序的假实现。</summary>
        private sealed class RecordingSteps : ILogStopSteps
        {
            internal readonly List<string> Calls = new List<string>();

            internal bool Throw;

            public void StopAccepting()
            {
                Enter("StopAccepting");
            }

            public void FlushToDisk()
            {
                Enter("FlushToDisk");
            }

            public void StopPackThread()
            {
                Enter("StopPackThread");
            }

            public void StopUploader()
            {
                Enter("StopUploader");
            }

            public void CloseHandles()
            {
                Enter("CloseHandles");
            }

            private void Enter(string name)
            {
                Calls.Add(name);
                if (Throw)
                {
                    throw new InvalidOperationException("boom: " + name);
                }
            }
        }

        /// <summary>
        /// 停止顺序必须是**停止接受新日志 → flush → 停打包线程 → 停上传线程 → 关句柄**。
        ///
        /// <para>每一步的位置都是有理由的，换任何一步都会坏掉一件事：</para>
        /// <para>· 采集不先停，flush 追的是一个还在涨的移动靶。</para>
        /// <para>· flush 不在停打包线程之前，缓冲里最后那段就永远写不出去了。</para>
        /// <para>· 上传线程不在关句柄之前停，它可能还在读 outbox 的文件。</para>
        /// <para>· 关句柄不在最后，泄漏的句柄会让下次进播放模式打不开卷。</para>
        /// <para>顺序错了不会有编译错误、也不会抛异常，只会在真机上悄悄丢日志 —— 所以要钉死。</para>
        /// </summary>
        [Test]
        public void StopSequence_RunsTheFiveStepsInTheBoundedOrder()
        {
            var steps = new RecordingSteps();

            LogStopSequence.Run(steps);

            CollectionAssert.AreEqual(
                new[] { "StopAccepting", "FlushToDisk", "StopPackThread", "StopUploader", "CloseHandles" },
                steps.Calls);
        }

        /// <summary>
        /// 某一步炸了，**后面的步骤照跑，异常不外泄**。
        /// <para>
        /// 最要紧的是最后那步：前面出什么事都必须把句柄关掉。
        /// 停机路径上抛出去的异常没有任何人能处理，只会把宿主的退出流程一起带走。
        /// </para>
        /// </summary>
        [Test]
        public void StopSequence_KeepsGoingWhenAStepThrows()
        {
            // 每一步失败都会走 LogSystem.InternalError → Debug.LogError（全局硬约束 6）。
            LogAssert.ignoreFailingMessages = true;

            var steps = new RecordingSteps();
            steps.Throw = true;

            Assert.DoesNotThrow(() => LogStopSequence.Run(steps));

            CollectionAssert.AreEqual(
                new[] { "StopAccepting", "FlushToDisk", "StopPackThread", "StopUploader", "CloseHandles" },
                steps.Calls,
                "一步失败不能拖住后面的 —— 尤其是关句柄那一步");
        }

        /// <summary>
        /// **重复调用不抛**：quit 与域重载会走同一条路径，Editor 里两者还可能接连发生。
        /// </summary>
        [Test]
        public void StopSequence_IsSafeToRunTwice()
        {
            var steps = new RecordingSteps();

            Assert.DoesNotThrow(() =>
            {
                LogStopSequence.Run(steps);
                LogStopSequence.Run(steps);
                LogStopSequence.Run(null);
            });

            Assert.AreEqual(10, steps.Calls.Count);
        }

        /// <summary>
        /// 假上传器：<c>Stop</c> 里 cancel 掉在飞的请求，而不是等它自己超时。
        /// </summary>
        private sealed class CancellableUploader : ILogUploader
        {
            private readonly CancellationTokenSource m_Cancel = new CancellationTokenSource();
            private readonly ManualResetEventSlim m_RequestEnded = new ManualResetEventSlim(false);

            internal bool RequestWasCancelled;

            public void Start()
            {
                var thread = new Thread(() =>
                {
                    // 一个 HttpTimeoutMs 的请求在飞。没有取消的话，它会一直挂到超时。
                    RequestWasCancelled = m_Cancel.Token.WaitHandle.WaitOne(new LogConfig().HttpTimeoutMs);
                    m_RequestEnded.Set();
                });

                thread.IsBackground = true;
                thread.Start();
            }

            public void Stop()
            {
                m_Cancel.Cancel();

                // 真实实现（S4）也要在这里等自己的线程收尾，但**必须是有界的**。
                m_RequestEnded.Wait(1000);
            }

            public void SetAddresses(string[] addrs)
            {
            }

            public void OnNetworkResumed()
            {
            }
        }

        /// <summary>
        /// 退出**不能卡满一次 HTTP 超时**：<see cref="ILogUploader.Stop"/> 必须能取消在飞的请求。
        /// <para>
        /// 09 号票才有真正的上传实现，这里验证的是接缝本身：停止顺序会调 Stop，
        /// 而一个"Stop 里 cancel"的实现能让整条退出路径远快于 <see cref="LogConfig.HttpTimeoutMs"/>。
        /// 换成"等它自己超时"的实现，这条断言会直接红给你看。
        /// </para>
        /// </summary>
        [Test]
        public void StopUploader_CancelsAnInFlightRequestInsteadOfWaitingOutTheHttpTimeout()
        {
            var uploader = new CancellableUploader();
            uploader.Start();

            // 停止顺序的第四步就是停上传线程 —— 这里让它停的是一个真的有请求在飞的上传器。
            var steps = new UploaderStopSteps(uploader);
            var elapsed = Stopwatch.StartNew();

            LogStopSequence.Run(steps);

            elapsed.Stop();

            Assert.IsTrue(steps.UploaderStopped);
            Assert.IsTrue(uploader.RequestWasCancelled, "在飞的请求必须是被取消的，不是自己超时结束的");
            Assert.Less(
                elapsed.ElapsedMilliseconds, new LogConfig().HttpTimeoutMs / 2,
                "退出卡满一个 HTTP 超时（10s）是不可接受的");
        }

        /// <summary>只把第四步接到一个真实的上传器上，其余四步什么也不做。</summary>
        private sealed class UploaderStopSteps : ILogStopSteps
        {
            private readonly ILogUploader m_Uploader;

            internal bool UploaderStopped;

            internal UploaderStopSteps(ILogUploader uploader)
            {
                m_Uploader = uploader;
            }

            public void StopAccepting()
            {
            }

            public void FlushToDisk()
            {
            }

            public void StopPackThread()
            {
            }

            public void StopUploader()
            {
                m_Uploader.Stop();
                UploaderStopped = true;
            }

            public void CloseHandles()
            {
            }
        }

        /// <summary>
        /// 08 号票之前，强制切批落在空实现上：<see cref="NullLogBatchSink"/> 已经实现了
        /// <see cref="ILogBatchSinkPersist"/>，因此调用顺序就是最终形态，
        /// 08 合入时只要让 S3 的 sink 实现同一个接口就能接上。
        /// </summary>
        [Test]
        public void TheNullSinkAlreadyProvidesTheForcedOutboxSeam()
        {
            var persist = NullLogBatchSink.Instance as ILogBatchSinkPersist;

            Assert.IsNotNull(persist, "接缝要留在空实现上，08 才能直接接上");
            Assert.DoesNotThrow(() => persist.Persist());
        }
    }
}
