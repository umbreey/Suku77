using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Umbreey.Tests
{
    /// <summary>
    /// 本地卷的 EditMode 测试（票 03 / specs 0001「本地卷」/ ADR-0001）。
    ///
    /// <para>
    /// <see cref="LogVolume"/> 刻意不碰任何 Unity API（persistentDataPath / productName
    /// 都是主线程预取后传进来的），所以它能在 EditMode 里对着一个临时目录直接跑真实 IO。
    /// 这里测的是**我们自己的决定**（卷名、共享模式、写什么、卷满怎么停），
    /// 不测 FileStream 的 IO 行为本身 —— 那是 BCL 的责任。
    /// </para>
    /// </summary>
    public class LogVolumeTests
    {
        private const string ProductName = "Niniko GO";
        private const string TraceId = "a1b2c3d4-1111-2222-3333-444455556666";

        private string m_Dir;

        [SetUp]
        public void SetUp()
        {
            m_Dir = Path.Combine(Path.GetTempPath(), "UmbreeyLogVolumeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(m_Dir);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(m_Dir))
                {
                    Directory.Delete(m_Dir, true);
                }
            }
            catch (IOException)
            {
                // 句柄没关干净的话这里会失败 —— 但那属于被测行为，交给对应的用例去断言。
            }
        }

        private static LogRecord Rec(string msg, LogType type = LogType.Log, string stack = "")
        {
            return LogRecord.Capture(0L, type, msg, stack, 0, false);
        }

        private static List<LogRecord> List(params LogRecord[] records)
        {
            return new List<LogRecord>(records);
        }

        // ================================================================================
        //  卷名
        // ================================================================================

        /// <summary>
        /// 卷名形如 <c>Niniko_GO_2026_09_08_a1b2c3d4.log</c>：产品名下划线化 + 日期 + traceId 前 8 位。
        /// <para>
        /// productName 是 "Niniko GO"，**含空格**。旧 LogWrite 的 deleteYesterday 从未生效，
        /// 根因就是写入时把空格换成了下划线、删除时又拿原名匹配 ——
        /// 所以清洗规则必须只有一处定义，票 04 解析卷名时引用的是同一个方法。
        /// </para>
        /// </summary>
        [Test]
        public void BuildFileName_UsesSanitizedProductDateAndTraceIdPrefix()
        {
            string name = LogVolume.BuildFileName(ProductName, new DateTime(2026, 9, 8), TraceId);

            Assert.AreEqual("Niniko_GO_2026_09_08_a1b2c3d4.log", name);
        }

        /// <summary>
        /// 卷名里的 traceId 段解决了 PC 多开抢同一个文件的问题，**不需要再加 PID 后缀**。
        /// 两次会话的卷名必须不同，否则"发一个卷 == 发完整一次运行"就不成立了。
        /// </summary>
        [Test]
        public void BuildFileName_DiffersBetweenSessionsOnTheSameDay()
        {
            var day = new DateTime(2026, 9, 8);

            string first = LogVolume.BuildFileName(ProductName, day, Guid.NewGuid().ToString("D"));
            string second = LogVolume.BuildFileName(ProductName, day, Guid.NewGuid().ToString("D"));

            Assert.AreNotEqual(first, second);
        }

        /// <summary>产品名里的非法文件名字符一律换成下划线，卷名永远是可创建的。</summary>
        [Test]
        public void BuildFileName_ReplacesCharactersThatCannotAppearInAFileName()
        {
            string name = LogVolume.BuildFileName("a/b:c*d", new DateTime(2026, 9, 8), TraceId);

            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in invalid)
            {
                Assert.IsFalse(name.IndexOf(c) >= 0, "卷名里不该出现非法字符 " + (int)c);
            }
        }

        // ================================================================================
        //  写入
        // ================================================================================

        private LogVolume OpenVolume(LogConfig cfg = null)
        {
            var volume = new LogVolume(cfg ?? new LogConfig(), m_Dir, ProductName, TraceId);
            volume.Open();
            return volume;
        }

        /// <summary>正文按行写进卷，内容原样（Log.cs 的时间前缀不剥离）。</summary>
        [Test]
        public void Write_AppendsEachRecordAsALine()
        {
            LogVolume volume = OpenVolume();
            volume.Write(List(Rec("first line"), Rec("second line")));
            volume.Close();

            string text = File.ReadAllText(volume.FilePath);

            StringAssert.Contains("first line", text);
            StringAssert.Contains("second line", text);
            Assert.Less(text.IndexOf("first line", StringComparison.Ordinal),
                text.IndexOf("second line", StringComparison.Ordinal), "写入顺序就是采集顺序");
        }

        /// <summary>
        /// 敏感记录**照常写进本地卷**（全局硬约束 7），而且哨兵已经被剥掉、正文含凭据。
        /// <para>
        /// 这是有意的：本地日志只在玩家自己的设备上，且是排查登录问题的必要材料；
        /// 拦截只发生在上传那一侧（<see cref="LogSystem.AppendUploadable"/>）。
        /// 把日志删掉的话就没法排查了 —— 策略是"保留本地、拦住上传"。
        /// </para>
        /// </summary>
        [Test]
        public void Write_KeepsSensitiveRecordsInTheVolume()
        {
            LogRecord sensitive = Rec("09/08 15:30:45.123: " + LogSystem.SensitiveTag + "token:abc123");
            Assert.IsTrue(sensitive.Sensitive, "前置条件：这条记录必须被识别成敏感");

            LogVolume volume = OpenVolume();
            volume.Write(List(sensitive));
            volume.Close();

            string text = File.ReadAllText(volume.FilePath);

            StringAssert.Contains("token:abc123", text, "凭据要留在本地卷里，这是排查登录问题的材料");
            StringAssert.Contains("09/08 15:30:45.123: ", text, "剥哨兵不能把前面的时间前缀一起切掉");
            Assert.IsFalse(text.Contains(LogSystem.SensitiveTag), "哨兵本身不该出现在卷里");
        }

        /// <summary>
        /// 异常的堆栈写进卷（那是排查现场的主体），普通 Log 的不写。
        /// <para>
        /// 项目的 ProjectSettings 对 LogType.Log 也开着 ScriptOnly 堆栈，每条 Debug.Log
        /// 都带一份调用栈；全写进去会把卷撑成几倍大而几乎没有排查价值。
        /// <see cref="LogRecord.Stack"/> 始终完整保留，上传侧要不要带是 S3 的事。
        /// </para>
        /// </summary>
        [Test]
        public void Write_IncludesStackForFailuresOnly()
        {
            LogVolume volume = OpenVolume();
            volume.Write(List(
                Rec("plain", LogType.Log, "stack-of-a-plain-log"),
                Rec("boom", LogType.Exception, "stack-of-an-exception")));
            volume.Close();

            string text = File.ReadAllText(volume.FilePath);

            StringAssert.Contains("stack-of-an-exception", text);
            Assert.IsFalse(text.Contains("stack-of-a-plain-log"));
        }

        /// <summary>
        /// ⚠️ 卷必须用 <c>FileShare.ReadWrite</c> 打开：票 11 的飞书一键发送要在
        /// **句柄常开的同时**并发读它。用默认的 FileShare.Read 会让一键发送直接读不到，
        /// 而且那个失败要等到接飞书的时候才会暴露出来。
        /// </summary>
        [Test]
        public void Open_AllowsConcurrentReadingWhileTheHandleStaysOpen()
        {
            LogVolume volume = OpenVolume();
            volume.Write(List(Rec("visible while the volume is still open")));

            // 不关卷，直接并发读 —— 这正是一键发送的姿势。
            using (var reader = new FileStream(
                volume.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var text = new StreamReader(reader))
            {
                StringAssert.Contains("visible while the volume is still open", text.ReadToEnd());
            }

            volume.Close();
        }

        /// <summary>
        /// 每次 Write 之后要 Flush()，字节当场进内核 —— 崩溃丢的是进程内存不是 OS 页缓存
        /// （ADR-0001）。上面那条并发读能读到内容，本身就依赖这次 Flush。
        /// <para>用的是 <c>Flush()</c> 而**不是** <c>Flush(true)</c>：后者是 fsync，我们不需要。</para>
        /// </summary>
        [Test]
        public void Write_FlushesSoTheBytesAreVisibleBeforeClose()
        {
            LogVolume volume = OpenVolume();
            volume.Write(List(Rec("flushed")));

            // 从另一个句柄读，而不是看文件系统的元数据：要断言的是"字节已经交给内核"，
            // 不是"目录项上的长度字段已经更新"。
            using (var reader = new FileStream(
                volume.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                Assert.Greater(reader.Length, 0, "Write 之后字节必须已经交给内核");
            }

            volume.Close();
        }

        /// <summary>
        /// 单卷超过 VolumeMaxBytes 时停止写入，并留下一行说明。
        /// <para>
        /// 没有这一行的话，卷看起来就是"日志突然断了"，分不清是崩溃还是写满
        /// （specs/0001「与限流的冲突」）。
        /// </para>
        /// </summary>
        [Test]
        public void Write_StopsAndLeavesANoticeWhenTheVolumeIsFull()
        {
            var cfg = new LogConfig();
            cfg.VolumeMaxBytes = 64;

            LogVolume volume = OpenVolume(cfg);
            volume.Write(List(Rec(new string('a', 100))));
            Assert.IsTrue(volume.IsFull);

            volume.Write(List(Rec("written after the volume is full")));
            volume.Close();

            string text = File.ReadAllText(volume.FilePath);

            StringAssert.Contains(LogVolume.FullNotice, text);
            Assert.IsFalse(text.Contains("written after the volume is full"), "卷满是硬停，之后完全静默");
        }

        /// <summary>
        /// Close 之后句柄真的放开了：域重载或退出播放模式后，下一次进播放模式必须能
        /// 重新打开同一个目录下的卷。句柄泄漏的表现正是"文件被占用"（票 03 的人工验收项）。
        /// </summary>
        [Test]
        public void Close_ReleasesTheHandleSoTheFileCanBeReopenedOrDeleted()
        {
            LogVolume volume = OpenVolume();
            volume.Write(List(Rec("session one")));
            volume.Close();

            Assert.DoesNotThrow(() => File.Delete(volume.FilePath));
        }

        /// <summary>Close 可以重复调用：quit 与域重载可能一前一后都触发一次停机。</summary>
        [Test]
        public void Close_IsIdempotent()
        {
            LogVolume volume = OpenVolume();
            volume.Close();

            Assert.DoesNotThrow(() => volume.Close());
        }

        /// <summary>还没 Open 就 Write 不能抛 —— 挂载失败时日志系统仍会被调用。</summary>
        [Test]
        public void Write_IsSafeBeforeOpenAndAfterClose()
        {
            var volume = new LogVolume(new LogConfig(), m_Dir, ProductName, TraceId);

            Assert.DoesNotThrow(() => volume.Write(List(Rec("before open"))));

            volume.Open();
            volume.Close();

            Assert.DoesNotThrow(() => volume.Write(List(Rec("after close"))));
        }

        /// <summary>非 ASCII 正文按 UTF-8 落盘，读回来一字不差。</summary>
        [Test]
        public void Write_EncodesTextAsUtf8()
        {
            const string text = "\u4e2d\u6587\u65e5\u5fd7\u6d4b\u8bd5";

            LogVolume volume = OpenVolume();
            volume.Write(List(Rec(text)));
            volume.Close();

            StringAssert.Contains(text, File.ReadAllText(volume.FilePath, System.Text.Encoding.UTF8));
        }
    }
}
