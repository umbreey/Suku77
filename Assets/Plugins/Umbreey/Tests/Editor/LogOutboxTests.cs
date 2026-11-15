using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Umbreey.Tests
{
    /// <summary>
    /// outbox 落盘与保留的 EditMode 测试（票 08 / specs 0003「outbox 文件」/ 契约 C3）。
    ///
    /// <para>
    /// 这里钉的是三件"错了才发现就来不及"的事：
    /// <list type="number">
    ///   <item><b>原子写</b> —— 写到一半被杀不能留下半个 JSON。而"进程被杀"恰恰是整套设计的核心场景。</item>
    ///   <item><b>删对文件</b> —— 与票 04 的卷保留一样，删错 = 丢日志。所以选删的那一半是纯函数，
    ///   每个分支直接摆出来断言。</item>
    ///   <item><b>容量是运行期实时上限</b> —— 启动时 49MB、运行中再写 20MB 就突破到 69MB，
    ///   这条只能靠"启动后继续写"的用例才测得出来。</item>
    /// </list>
    /// </para>
    /// <para>
    /// ⚠️ 用例一律**显式关掉** <see cref="LogOutbox.ClearAllOnStartup"/>：测试自己跑在 Editor 里，
    /// 而 Editor 的默认行为是"每次启动清空 outbox"，不关的话保留策略一条都测不到，
    /// 却全都会"通过"。
    /// </para>
    /// </summary>
    public class LogOutboxTests
    {
        /// <summary>固定的"当前时刻"。测时间相关的逻辑不能用真实的 now。</summary>
        private static readonly DateTime Now = new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);

        private static readonly DateTime UnixEpochUtc =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private const string TraceA = "aaaaaaaa";
        private const string TraceB = "bbbbbbbb";

        private string m_Dir;

        [SetUp]
        public void SetUp()
        {
            m_Dir = Path.Combine(Path.GetTempPath(), "UmbreeyLogOutboxTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(m_Dir);
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;

            try
            {
                if (Directory.Exists(m_Dir))
                {
                    Directory.Delete(m_Dir, true);
                }
            }
            catch (IOException)
            {
            }
        }

        // ────────────────────────────────────────────────────────────────────────────────
        //  helpers
        // ────────────────────────────────────────────────────────────────────────────────

        /// <summary>outbox 是日志目录的子目录（契约 C3），实例自己拼，这里跟着拼一份用于断言。</summary>
        private string OutboxDir
        {
            get { return Path.Combine(m_Dir, LogOutbox.DirectoryName); }
        }

        private static long UnixSec(DateTime utc)
        {
            return (long)(utc - UnixEpochUtc).TotalSeconds;
        }

        /// <summary>按生产代码的同一个方法造文件名 —— 测试不自己拼名字，否则两边会各自漂移。</summary>
        private static string Name(int daysAgo, string trace = TraceA, int seq = 0)
        {
            return LogOutbox.BuildFileName(UnixSec(Now.AddDays(-daysAgo)), trace, seq);
        }

        private static LogOutbox.LogOutboxFile Entry(string name, long bytes = 1000)
        {
            return new LogOutbox.LogOutboxFile(name, bytes);
        }

        private static List<LogOutbox.LogOutboxFile> Files(params LogOutbox.LogOutboxFile[] entries)
        {
            return new List<LogOutbox.LogOutboxFile>(entries);
        }

        /// <summary>两条规则都撑到"不可能触发"，用例再把要验的那一条调回来。</summary>
        private static LogConfig Relaxed()
        {
            var cfg = new LogConfig();
            cfg.OutboxMaxBytes = long.MaxValue;
            cfg.OutboxKeepDays = int.MaxValue;
            return cfg;
        }

        /// <summary>关掉 Editor 清空，其余照默认。</summary>
        private LogOutbox NewOutbox(LogConfig cfg, string trace = TraceA)
        {
            var outbox = new LogOutbox(cfg, m_Dir, trace);
            outbox.ClearAllOnStartup = false;
            return outbox;
        }

        /// <summary>在 outbox 目录里预置一个文件，并把 mtime 推到多开豁免窗口之外。</summary>
        private string Preset(string name, int bytes = 1000, double mtimeMinutesAgo = 60)
        {
            Directory.CreateDirectory(OutboxDir);
            string path = Path.Combine(OutboxDir, name);
            File.WriteAllBytes(path, new byte[bytes]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-mtimeMinutesAgo));
            return path;
        }

        private long DiskBytes()
        {
            long total = 0;
            string[] paths = Directory.GetFiles(OutboxDir);
            for (int i = 0; i < paths.Length; i++)
            {
                total += new FileInfo(paths[i]).Length;
            }

            return total;
        }

        // ================================================================================
        //  1. 保留策略纯函数（票 08「Editor 内自动」第 1 组）
        // ================================================================================

        /// <summary>
        /// 容量超限：删到装得下为止，**删的是最旧的**。
        /// <para>
        /// 最新的批次离崩溃现场最近、信息密度最高，丢最新等于把最该看的那部分丢掉。
        /// </para>
        /// </summary>
        [Test]
        public void SelectForDeletion_DeletesTheOldestWhenOverCapacity()
        {
            LogConfig cfg = Relaxed();
            cfg.OutboxMaxBytes = 3000;

            // 5 个 × 1000 字节 = 5000，超 2000 → 该删最旧的两个。
            var files = Files(
                Entry(Name(4)), Entry(Name(3)), Entry(Name(2)), Entry(Name(1)), Entry(Name(0)));

            List<string> doomed = LogOutbox.SelectForDeletion(files, Now, cfg);

            Assert.AreEqual(2, doomed.Count, "只该删到刚好装得下，不该多删");
            Assert.AreEqual(Name(4), doomed[0], "最旧的先删");
            Assert.AreEqual(Name(3), doomed[1], "其次旧的跟上");
        }

        /// <summary>
        /// TTL 过期：<see cref="LogConfig.OutboxKeepDays"/> 之外的删掉，保留期内的不动。
        /// <para>
        /// 用的是参数总表的默认值（7），不是测试自己写死的数字 ——
        /// 这条断言同时钉住"票里那个 7 确实是从 LogConfig 里取的"。
        /// </para>
        /// <para>
        /// TTL 是**防止永久失败的批次无限滞留的唯一机制**：重试预算是内存态、进程重启就清零，
        /// 拦不住一个因配置错误而永远失败的批次。
        /// </para>
        /// </summary>
        [Test]
        public void SelectForDeletion_DeletesExpiredBatchesByTtl()
        {
            LogConfig cfg = Relaxed();
            cfg.OutboxKeepDays = new LogConfig().OutboxKeepDays;

            string expired = Name(cfg.OutboxKeepDays + 1);
            string onTheEdge = Name(cfg.OutboxKeepDays);
            string fresh = Name(1);

            List<string> doomed = LogOutbox.SelectForDeletion(
                Files(Entry(expired), Entry(onTheEdge), Entry(fresh)), Now, cfg);

            CollectionAssert.Contains(doomed, expired, "过期的必须删");
            CollectionAssert.Contains(doomed, onTheEdge, "年龄达到 KeepDays 就算过期（>=，不是 >）");
            CollectionAssert.DoesNotContain(doomed, fresh, "保留期内的一根汗毛都不许动");
        }

        /// <summary>
        /// 两条规则同时命中：**取并集、不重复删**，且结果按从旧到新排。
        /// <para>
        /// 顺序是先 TTL 后容量：过期的反正要删，让它先退出预算，
        /// 容量这一条才不会多删一个还活着的批次。
        /// </para>
        /// </summary>
        [Test]
        public void SelectForDeletion_TakesTheUnionWhenBothRulesHit()
        {
            var cfg = new LogConfig();
            cfg.OutboxKeepDays = 7;
            cfg.OutboxMaxBytes = 2000;

            string expired = Name(8);
            string oldest = Name(3);
            string middle = Name(2);
            string newest = Name(1);

            List<string> doomed = LogOutbox.SelectForDeletion(
                Files(Entry(expired), Entry(oldest), Entry(middle), Entry(newest)), Now, cfg);

            // 过期的先出局（4000 → 3000），仍超 2000，于是再删一个最旧的存活者。
            Assert.AreEqual(2, doomed.Count, "并集里不许有重复项");
            Assert.AreEqual(expired, doomed[0], "结果按从旧到新排");
            Assert.AreEqual(oldest, doomed[1], "容量这一条删的仍然是最旧的存活者");
            CollectionAssert.DoesNotContain(doomed, middle);
            CollectionAssert.DoesNotContain(doomed, newest);
        }

        // ================================================================================
        //  2. 文件名解析（票 08「Editor 内自动」第 2 组）
        // ================================================================================

        /// <summary>
        /// 能从文件名取出 createSec，并且**文件名的形状本身就是契约 C3 的那一行**：
        /// <c>{createUnixSec:D10}-{trace_id 前 8 位}-{seq:D5}.json</c>。
        /// <para>
        /// 这里连字面量一起断言：格式一旦漂了，"字典序 == 时间序 == FIFO"就不再成立，
        /// 而那是靠排序断言看不出来的（同一套错格式内部仍然自洽）。
        /// </para>
        /// </summary>
        [Test]
        public void TryParseCreateUnixSec_ExtractsTheCreateSecFromAWellFormedName()
        {
            string name = LogOutbox.BuildFileName(1757000000L, "aabbccdd-1111-2222", 7);

            Assert.AreEqual("1757000000-aabbccdd-00007.json", name, "文件名格式就是契约 C3");

            long createSec;
            Assert.IsTrue(LogOutbox.TryParseCreateUnixSec(name, out createSec));
            Assert.AreEqual(1757000000L, createSec);
        }

        /// <summary>
        /// 不合规的文件名**被忽略而不是抛异常**。
        /// <para>
        /// 这个目录里可能躺着别人留下的文件、上一版客户端的文件、拷进来的文件。
        /// 一个畸形名字掀翻整次清理 = 从此一个文件都删不掉 = 一路堆到容量上限。
        /// </para>
        /// </summary>
        [Test]
        public void TryParseCreateUnixSec_IgnoresMalformedNamesInsteadOfThrowing()
        {
            string[] malformed =
            {
                null,
                "",
                "whatever.json",
                "1757000000-aabbccdd-00007.tmp",   // 扫描只认 .json
                "1757000000-aabbccdd.json",        // 少一段
                "175700-aabbccdd-00007.json",      // createSec 不足 10 位，字典序不再等于时间序
                "abcdefghij-aabbccdd-00007.json",  // 不是数字
                "1757000000-aabbccdd-xxxxx.json",  // seq 不是数字
                "99999999999999999999-aa-00001.json", // 大到按日期算年龄会溢出
            };

            var accepted = new List<string>();

            // 先只关心"不抛" —— 断言分开写，失败时才分得清是抛了还是认错了。
            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < malformed.Length; i++)
                {
                    long createSec;
                    if (LogOutbox.TryParseCreateUnixSec(malformed[i], out createSec))
                    {
                        accepted.Add(malformed[i]);
                    }
                }
            });

            CollectionAssert.IsEmpty(accepted, "这些名字一个都不该被认领");
        }

        // ================================================================================
        //  3. 排序（票 08「Editor 内自动」第 3 组）
        // ================================================================================

        /// <summary>
        /// 一组乱序文件名排序后是**时间升序**。
        /// <para>
        /// 同一秒内的多个批次靠 seq 分先后 —— 攒批窗口 3 秒、一次交换能切出好几批，
        /// 同秒多批是常态而不是边角情况。
        /// </para>
        /// </summary>
        [Test]
        public void SortOldestFirst_OrdersShuffledNamesByTime()
        {
            long sec = UnixSec(Now);

            var files = Files(
                Entry(LogOutbox.BuildFileName(sec, TraceA, 2)),
                Entry(Name(0)),
                Entry(LogOutbox.BuildFileName(sec, TraceA, 1)),
                Entry(Name(5)),
                Entry(Name(3)));

            LogOutbox.SortOldestFirst(files);

            long previous = long.MinValue;
            var order = new List<string>();
            for (int i = 0; i < files.Count; i++)
            {
                long createSec;
                Assert.IsTrue(LogOutbox.TryParseCreateUnixSec(files[i].Name, out createSec));
                Assert.GreaterOrEqual(createSec, previous, "排序后必须是时间升序");
                previous = createSec;
                order.Add(files[i].Name);
            }

            Assert.AreEqual(Name(5), order[0], "最旧的排最前");
            Assert.AreEqual(
                LogOutbox.BuildFileName(sec, TraceA, 2), order[order.Count - 1],
                "同一秒内 seq 大的排后面");
        }

        // ================================================================================
        //  4. 原子写（票 08「Editor 内自动」第 4、5 组）
        // ================================================================================

        /// <summary>
        /// 写完之后目录里**只有 <c>.json</c>、没有 <c>.tmp</c>**，且内容一个字节不差。
        /// <para>
        /// 内容是 payload_raw 明文、**不压缩**（压缩只在发送那一刻做，是票 09 的事），
        /// 所以这里能直接按字节比。
        /// </para>
        /// </summary>
        [Test]
        public void Submit_LeavesOnlyJsonAndNoTmp()
        {
            LogOutbox outbox = NewOutbox(Relaxed());
            byte[] payload = System.Text.Encoding.UTF8.GetBytes("{\"v\":1,\"logs\":[]}");

            string name = outbox.SubmitBytes(payload);

            Assert.IsNotNull(name, "落盘应当成功");
            string[] files = Directory.GetFiles(OutboxDir);
            Assert.AreEqual(1, files.Length, "一个批次一个文件");
            Assert.AreEqual(LogOutbox.Extension, Path.GetExtension(files[0]), "只能留下 .json");
            CollectionAssert.IsEmpty(
                Directory.GetFiles(OutboxDir, "*" + LogOutbox.TempExtension),
                "File.Move 之后不许还留着 .tmp");
            CollectionAssert.AreEqual(payload, File.ReadAllBytes(files[0]), "落盘的就是 payload_raw 本身");
            Assert.AreEqual(payload.Length, outbox.Bytes, "索引要跟着增量更新");
        }

        /// <summary>
        /// 启动清理会删掉**遗留的** <c>.tmp</c>（上次进程写到一半被杀留下的半个文件）。
        /// <para>
        /// 不清掉的话它不会被扫描认走（扫描只认 .json），但会一直占着磁盘。
        /// </para>
        /// </summary>
        [Test]
        public void Start_DeletesAStaleTmp()
        {
            string stale = Preset("1757000000-aabbccdd-00001.tmp", 512, 60);

            NewOutbox(Relaxed()).Start();

            Assert.IsFalse(File.Exists(stale), "过了宽限期的 .tmp 是上次被杀留下的，该删");
        }

        /// <summary>
        /// **多开豁免**：mtime 在 <see cref="LogVolume.ActiveVolumeGraceSec"/> 之内的 <c>.tmp</c> 不许删 ——
        /// PC 多开时那可能是**另一个实例正在写**的临时文件，删掉会让对方的批次凭空消失。
        /// <para>
        /// ⚠️ 这里断言的宽限期直接取 <see cref="LogVolume.ActiveVolumeGraceSec"/>：
        /// 契约 C3 补记要求 outbox 的 .tmp 清理与卷保留的活跃卷豁免**必须同值**，
        /// 各写一份就会出现"一边保、一边删"。测试引用同一个常量，两处漂移会立刻在这里现形。
        /// </para>
        /// </summary>
        [Test]
        public void Start_KeepsATmpAnotherInstanceMayBeWriting()
        {
            double insideGraceMinutes = LogVolume.ActiveVolumeGraceSec / 60.0 / 2.0;
            string active = Preset("1757000000-bbbbbbbb-00001.tmp", 512, insideGraceMinutes);
            string stale = Preset(
                "1757000000-cccccccc-00001.tmp", 512, LogVolume.ActiveVolumeGraceSec / 60.0 + 1.0);

            NewOutbox(Relaxed()).Start();

            Assert.IsTrue(File.Exists(active), "宽限期内的 .tmp 可能是另一个实例正在写的，不许删");
            Assert.IsFalse(File.Exists(stale), "宽限期外的照删，否则这个用例什么也没证明");
        }

        // ================================================================================
        //  5. 扫描（票 08「Editor 内自动」第 6 组）
        // ================================================================================

        /// <summary>
        /// 扫描**只认 <c>.json</c>**（契约 C3）：预置的 <c>.json</c> 能被返回，<c>.tmp</c> 不被返回。
        /// <para>
        /// <c>.tmp</c> 可能是半个文件，发出去必然 code=1 → 退避重试 5 次 → 丢弃，
        /// 白费 5 次请求，日志还是丢了。
        /// </para>
        /// </summary>
        [Test]
        public void ScanFiles_ReturnsJsonAndIgnoresTmp()
        {
            Preset(Name(1), 100);
            Preset("1757000000-aabbccdd-00001.tmp", 100);
            Preset("someone-elses-file.txt", 100);

            List<LogOutbox.LogOutboxFile> found = LogOutbox.ScanFiles(OutboxDir);

            Assert.AreEqual(1, found.Count, "只该认领自己写的 .json");
            Assert.AreEqual(Name(1), found[0].Name);
            Assert.AreEqual(100, found[0].Bytes, "大小要带出来，容量预算靠它");
        }

        // ================================================================================
        //  6. 我要求的三条
        // ================================================================================

        /// <summary>
        /// **容量是运行期的实时上限，不是"启动时扫一遍"**（specs 0003）。
        ///
        /// <para>
        /// 场景就是那句话本身：启动时已经接近上限，运行中继续写。
        /// 只在启动时管一次的实现会一路涨上去（49MB + 20MB = 69MB），
        /// 这里断言**每写一批之后**磁盘占用都没突破 <see cref="LogConfig.OutboxMaxBytes"/>。
        /// </para>
        /// <para>
        /// 淘汰会走 <c>Debug.LogError</c> 报"丢了几批"（全局硬约束 6），故放行失败消息。
        /// </para>
        /// </summary>
        [Test]
        public void Capacity_IsEnforcedWhileRunningNotOnlyAtStartup()
        {
            LogAssert.ignoreFailingMessages = true;

            LogConfig cfg = Relaxed();
            cfg.OutboxMaxBytes = 5000;

            // 启动时 4000/5000 —— 接近上限但没超，启动清理不该删任何东西。
            long baseSec = UnixSec(DateTime.UtcNow) - 10;
            for (int i = 0; i < 4; i++)
            {
                Preset(LogOutbox.BuildFileName(baseSec, TraceA, i), 1000);
            }

            LogOutbox outbox = NewOutbox(cfg, TraceB);
            outbox.Start();

            Assert.AreEqual(4000, outbox.Bytes, "启动索引要把现存文件算进来，且这时不该删东西");

            var payload = new byte[1000];
            for (int i = 0; i < 5; i++)
            {
                Assert.IsNotNull(outbox.SubmitBytes(payload), "淘汰之后这一批仍然要写进去");
                Assert.LessOrEqual(
                    outbox.Bytes, cfg.OutboxMaxBytes, "索引口径不许突破上限");
                Assert.LessOrEqual(
                    DiskBytes(), cfg.OutboxMaxBytes, "磁盘上的真实占用同样不许突破上限");
            }

            // 淘汰的是最旧的：启动时那 4 个（trace aaaaaaaa、更早的秒）应当先出局。
            List<string> pending = outbox.PendingNames();
            for (int i = 0; i < pending.Count; i++)
            {
                StringAssert.Contains(TraceB, pending[i], "留下的应当是新写的那些，最旧的先被丢掉");
            }
        }

        /// <summary>
        /// **时钟回拨时文件名不撞车** —— 这是 traceId 段存在的理由（契约 C3 / 票 08）。
        ///
        /// <para>
        /// 场景：玩家把系统时间往回调，新会话的 createSec 变小，正好落回上次会话用过的那一秒；
        /// 而 seq 是**进程内**计数器，两个会话都从 0 开始。没有 traceId 段的话两个文件同名，
        /// 后写的那个会把前一次会话的批次直接覆盖掉。
        /// </para>
        /// <para>
        /// 同一个场景也覆盖 PC 多开（两个实例 traceId 不同），**因此不需要再加 PID 后缀**。
        /// </para>
        /// </summary>
        [Test]
        public void FileNames_DoNotCollideWhenTheClockGoesBackwards()
        {
            LogOutbox first = NewOutbox(Relaxed(), "11111111-aaaa-bbbb");
            LogOutbox second = NewOutbox(Relaxed(), "22222222-cccc-dddd");

            const long rewound = 1700000000L;

            // 两次"会话"落在同一秒、都是本进程的第 0 批 —— 除了 traceId 段之外全都一样。
            string a = first.SubmitAt(new byte[] { 1 }, rewound);
            string b = second.SubmitAt(new byte[] { 2 }, rewound);

            Assert.AreNotEqual(a, b, "只靠 seq 是撞的，隔开它们的是 traceId 段");
            Assert.AreEqual(2, Directory.GetFiles(OutboxDir).Length, "谁也不许覆盖谁");
            Assert.AreEqual(1, File.ReadAllBytes(Path.Combine(OutboxDir, a))[0]);
            Assert.AreEqual(2, File.ReadAllBytes(Path.Combine(OutboxDir, b))[0]);
        }

        // ================================================================================
        //  7. 附带：Editor 特例与失败路径
        // ================================================================================

        /// <summary>
        /// **Editor 下每次启动清空 outbox**（票 08）—— Editor 不上传，留着只会堆到上限，
        /// 而它唯一的用途是让开发者看一眼批次格式。
        /// <para>
        /// 清空只针对 <c>.json</c>；<c>.tmp</c> 仍然走多开豁免那条路（另一个实例可能正在写它），
        /// 这条不因平台而改。
        /// </para>
        /// </summary>
        [Test]
        public void Start_ClearsEveryBatchWhenClearAllOnStartupIsOn()
        {
            string kept = Preset(Name(0), 100);
            string alsoKept = Preset(Name(1), 100);

            var outbox = new LogOutbox(Relaxed(), m_Dir, TraceA);
            outbox.ClearAllOnStartup = true;
            outbox.Start();

            Assert.IsFalse(File.Exists(kept), "Editor 每次启动清空 outbox");
            Assert.IsFalse(File.Exists(alsoKept));
            Assert.AreEqual(0, outbox.Bytes, "清空之后索引也要归零");
        }

        /// <summary>
        /// 目录用不了（这里让 outbox 的位置上躺着一个同名**文件**）时，
        /// 落盘**不抛异常**、只是这一批没写成。
        /// <para>
        /// 本类跑在打包线程上，异常穿透过去会让打包线程直接死掉，**本地卷也跟着停** ——
        /// 那是比"这一批没上传"严重得多的事故。失败本身按全局硬约束 6 记一条 Internal 诊断日志。
        /// </para>
        /// </summary>
        [Test]
        public void Submit_DoesNotThrowWhenTheDirectoryIsUnusable()
        {
            LogAssert.ignoreFailingMessages = true;

            File.WriteAllText(OutboxDir, "not a directory");

            LogOutbox outbox = NewOutbox(Relaxed());

            string name = null;
            Assert.DoesNotThrow(() => name = outbox.SubmitBytes(new byte[] { 1, 2, 3 }));
            Assert.IsNull(name, "写不成就如实返回 null，不许假装成功");
        }

        /// <summary>
        /// <see cref="LogOutbox.Delete"/> 同步维护索引 —— 票 09 发送成功后删文件走的就是它。
        /// <para>
        /// 绕过它直接 <c>File.Delete</c> 会让索引虚高，容量上限从此按一个虚假的占用来算：
        /// 明明还空着却一直淘汰，或者反过来。**索引是容量的事实源，它必须和磁盘一致。**
        /// </para>
        /// </summary>
        [Test]
        public void Delete_KeepsTheIndexInSyncWithTheDirectory()
        {
            LogOutbox outbox = NewOutbox(Relaxed());
            string first = outbox.SubmitBytes(new byte[100]);
            string second = outbox.SubmitBytes(new byte[250]);

            Assert.AreEqual(350, outbox.Bytes);
            Assert.AreEqual(2, outbox.Count);

            Assert.IsTrue(outbox.Delete(first));

            Assert.AreEqual(250, outbox.Bytes, "删掉的字节要从索引里减掉");
            Assert.AreEqual(1, outbox.Count);
            CollectionAssert.AreEqual(new[] { second }, outbox.PendingNames(), "剩下的仍按 FIFO 顺序");
            Assert.IsFalse(File.Exists(Path.Combine(OutboxDir, first)));
        }

        /// <summary>
        /// 增量维护的索引与**重扫目录**的结果一致（specs 0003 的 Testing Decisions 第 6 条）。
        /// <para>
        /// 这条是前面所有容量断言的地基：索引一旦和磁盘对不上，
        /// "运行期实时上限"就只是在对一个虚构的数字做算术。
        /// </para>
        /// </summary>
        [Test]
        public void Index_AgreesWithARescanOfTheDirectory()
        {
            LogAssert.ignoreFailingMessages = true;

            LogConfig cfg = Relaxed();
            cfg.OutboxMaxBytes = 3000;

            LogOutbox outbox = NewOutbox(cfg);
            for (int i = 0; i < 8; i++)
            {
                outbox.SubmitBytes(new byte[500]);
            }

            List<LogOutbox.LogOutboxFile> scanned = LogOutbox.ScanFiles(OutboxDir);

            long scannedBytes = 0;
            var scannedNames = new List<string>();
            for (int i = 0; i < scanned.Count; i++)
            {
                scannedBytes += scanned[i].Bytes;
                scannedNames.Add(scanned[i].Name);
            }

            Assert.AreEqual(scannedBytes, outbox.Bytes, "索引的字节数必须等于目录里的真实占用");
            CollectionAssert.AreEqual(scannedNames, outbox.PendingNames(), "文件集合与顺序都必须一致");
        }
    }
}
