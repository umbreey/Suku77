using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Umbreey.Tests
{
    /// <summary>
    /// 双缓冲与背压的 EditMode 测试（票 03 / specs 0001「缓冲与打包线程」）。
    ///
    /// <para>
    /// specs/0001 的「Testing Decisions」把它列为最值得测的接缝之一：
    /// 输入是"一串记录 + 上限"，输出是"交换后拿到的列表与丢弃计数"，纯内存、无 IO、无 Unity API。
    /// </para>
    /// <para>
    /// 这里**不测唤醒时序**（那是时间相关的，脆弱且价值低），只测数据语义。
    /// </para>
    /// </summary>
    public class LogBufferTests
    {
        private const int RecordSize = 200;

        /// <summary>
        /// 直接构造记录并写死 <see cref="LogRecord.Size"/>，不走 Capture ——
        /// 背压逻辑只关心 Size 这个数，让它依赖字节估算会把两件事绑在一起。
        /// </summary>
        private static LogRecord Rec(string msg, int size)
        {
            return new LogRecord
            {
                TsUnixMs = 0L,
                Type = LogType.Log,
                Msg = msg,
                Stack = "",
                Size = size,
                CtxVer = 0,
                Internal = false,
                Sensitive = false,
            };
        }

        /// <summary>
        /// 立刻拷一份出来。Swap 返回的是内部复用的列表，**下一次 Swap 之后就不再有效** ——
        /// 直接持有它做后续断言会写出一个时灵时不灵的测试。
        /// </summary>
        private static List<LogRecord> Copy(IReadOnlyList<LogRecord> records)
        {
            var copy = new List<LogRecord>(records.Count);
            for (int i = 0; i < records.Count; i++)
            {
                copy.Add(records[i]);
            }

            return copy;
        }

        private static LogBuffer NewBuffer(int limitBytes)
        {
            var cfg = new LogConfig();
            cfg.BufferLimitBytes = limitBytes;
            return new LogBuffer(cfg);
        }

        /// <summary>
        /// 不超限时一条不丢，顺序就是采集顺序（FIFO）。
        /// </summary>
        [Test]
        public void Swap_ReturnsEverythingInOrderWhenUnderTheLimit()
        {
            LogBuffer buffer = NewBuffer(RecordSize * 10);
            for (int i = 0; i < 5; i++)
            {
                buffer.Add(Rec(i.ToString(), RecordSize));
            }

            int dropped;
            List<LogRecord> swapped = Copy(buffer.Swap(out dropped));

            Assert.AreEqual(0, dropped);
            Assert.AreEqual(5, swapped.Count);
            for (int i = 0; i < 5; i++)
            {
                Assert.AreEqual(i.ToString(), swapped[i].Msg);
            }
        }

        /// <summary>
        /// 超限时丢的是**最旧**的 —— 队列是 FIFO，最靠近崩溃的是最新的那几条，
        /// 那才是排查现场最需要的（specs/0001「缓冲与打包线程」）。
        /// <para>
        /// 反过来写（丢最新）同样能让缓冲不超限、同样跑得动，
        /// 只有到了事故现场才会发现崩溃前最后几秒的日志全没了。
        /// </para>
        /// </summary>
        [Test]
        public void Add_DropsTheOldestRecordsWhenOverTheLimit()
        {
            // 上限恰好装 5 条：加满 10 条应当只剩最后 5 条。
            LogBuffer buffer = NewBuffer(RecordSize * 5);
            for (int i = 0; i < 10; i++)
            {
                buffer.Add(Rec(i.ToString(), RecordSize));
            }

            int dropped;
            List<LogRecord> swapped = Copy(buffer.Swap(out dropped));

            Assert.AreEqual(5, swapped.Count);
            Assert.AreEqual("5", swapped[0].Msg, "留下的必须是最新的那批，不是最旧的那批");
            Assert.AreEqual("9", swapped[swapped.Count - 1].Msg);

            for (int i = 0; i < 5; i++)
            {
                string gone = i.ToString();
                Assert.IsFalse(
                    swapped.Exists(r => r.Msg == gone), "第 " + gone + " 条是最旧的，应当已经被丢掉");
            }
        }

        /// <summary>丢弃计数必须准确 —— 它是"这次运行到底丢没丢日志"的唯一线索。</summary>
        [Test]
        public void Swap_ReportsTheExactDroppedCount()
        {
            LogBuffer buffer = NewBuffer(RecordSize * 5);
            for (int i = 0; i < 10; i++)
            {
                buffer.Add(Rec(i.ToString(), RecordSize));
            }

            int dropped;
            buffer.Swap(out dropped);

            Assert.AreEqual(5, dropped);
        }

        /// <summary>丢弃计数在交换时被清零，不会在下一轮里被重复报一遍。</summary>
        [Test]
        public void Swap_ResetsTheDroppedCount()
        {
            LogBuffer buffer = NewBuffer(RecordSize * 2);
            for (int i = 0; i < 6; i++)
            {
                buffer.Add(Rec(i.ToString(), RecordSize));
            }

            int first;
            buffer.Swap(out first);

            int second;
            buffer.Swap(out second);

            Assert.AreEqual(4, first);
            Assert.AreEqual(0, second, "丢弃计数只报一次");
        }

        /// <summary>
        /// 交换后活动缓冲被清空：下一轮拿到的只有交换之后新加的记录。
        /// <para>
        /// 漏掉这条清空，每一轮都会把上一轮的记录再写一遍 ——
        /// 本地卷里会出现指数级的重复，而且不报任何错。
        /// </para>
        /// </summary>
        [Test]
        public void Swap_ClearsTheActiveBufferSoRecordsAreNeverWrittenTwice()
        {
            LogBuffer buffer = NewBuffer(RecordSize * 100);
            buffer.Add(Rec("old", RecordSize));

            int dropped;
            Copy(buffer.Swap(out dropped));

            buffer.Add(Rec("new", RecordSize));
            List<LogRecord> second = Copy(buffer.Swap(out dropped));

            Assert.AreEqual(1, second.Count);
            Assert.AreEqual("new", second[0].Msg);
        }

        /// <summary>缓冲空着时交换出来的是空列表，不是 null —— 调用方不必判空。</summary>
        [Test]
        public void Swap_ReturnsAnEmptyListWhenNothingWasCaptured()
        {
            LogBuffer buffer = NewBuffer(RecordSize * 10);

            int dropped;
            IReadOnlyList<LogRecord> swapped = buffer.Swap(out dropped);

            Assert.IsNotNull(swapped);
            Assert.AreEqual(0, swapped.Count);
            Assert.AreEqual(0, dropped);
        }

        /// <summary>
        /// 累计字节在交换时归零：交换过的记录已经不在活动缓冲里，
        /// 不能再算进下一轮的背压预算，否则缓冲会越来越早地开始丢日志。
        /// </summary>
        [Test]
        public void Swap_ResetsTheByteAccountingSoTheNextRoundStartsFromZero()
        {
            LogBuffer buffer = NewBuffer(RecordSize * 5);
            for (int i = 0; i < 5; i++)
            {
                buffer.Add(Rec("first-" + i, RecordSize));
            }

            int dropped;
            buffer.Swap(out dropped);
            Assert.AreEqual(0, dropped);

            for (int i = 0; i < 5; i++)
            {
                buffer.Add(Rec("second-" + i, RecordSize));
            }

            List<LogRecord> second = Copy(buffer.Swap(out dropped));

            Assert.AreEqual(0, dropped, "上一轮的字节不该还占着这一轮的预算");
            Assert.AreEqual(5, second.Count);
        }

        /// <summary>
        /// 单条就超过上限的巨型记录（比如一份爆栈）**留下来**，不被自己触发的背压丢掉。
        /// <para>
        /// 背压的立意是保住最新的记录；把刚到的那条丢掉，等于让缓冲对最重要的一条日志
        /// 最不友好 —— 而一条爆栈异常往往正是崩溃现场本身。
        /// </para>
        /// </summary>
        [Test]
        public void Add_KeepsTheNewestRecordEvenWhenItAloneExceedsTheLimit()
        {
            LogBuffer buffer = NewBuffer(RecordSize);
            buffer.Add(Rec("small", RecordSize));
            buffer.Add(Rec("huge", RecordSize * 10));

            int dropped;
            List<LogRecord> swapped = Copy(buffer.Swap(out dropped));

            Assert.AreEqual(1, swapped.Count);
            Assert.AreEqual("huge", swapped[0].Msg);
            Assert.AreEqual(1, dropped);
        }
    }
}
