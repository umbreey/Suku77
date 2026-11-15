using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Umbreey.Tests
{
    /// <summary>
    /// 卷保留策略的 EditMode 测试（票 04 / specs 0001「本地卷」）。
    ///
    /// <para>
    /// 这是整个日志系统里**最容易写错、错了后果最严重的一段：删错 = 丢日志**，
    /// 而且丢的是玩家设备上唯一的一份。所以清理被拆成「纯函数选、IO 照着删」两半，
    /// 这里的绝大多数用例打的都是那个纯函数 <see cref="LogVolume.SelectForDeletion"/> ——
    /// 输入「文件列表 + 当前时刻 + 配置 + 当前卷名」，输出「该删哪些」，
    /// 不碰文件系统，所以每个分支都能直接摆出来。
    /// </para>
    /// <para>
    /// 用例一律**只配置自己要验的那条规则**，其余两条撑到不可能触发 ——
    /// 否则一条断言失败时分不清是哪条规则错了。
    /// </para>
    /// </summary>
    public class LogRetentionTests
    {
        private const string ProductName = "Niniko GO";

        /// <summary>固定的"当前时刻"。测时间相关的逻辑不能用真实的 now。</summary>
        private static readonly DateTime Now = new DateTime(2026, 9, 8, 12, 0, 0);

        /// <summary>当前会话的卷：今天、trace 前缀 <c>cccccccc</c>。多数用例里它必须活下来。</summary>
        private static string CurrentName
        {
            get { return Vol(0, "cccccccc"); }
        }

        /// <summary>按生产代码的同一个方法造卷名 —— 测试不自己拼名字，否则两边会各自漂移。</summary>
        private static string Vol(int daysAgo, string trace8)
        {
            return LogVolume.BuildFileName(ProductName, Now.AddDays(-daysAgo), trace8);
        }

        /// <summary>旧格式卷名：<c>{product}_{date}[_{alias}].log</c>，由 deleteYesterday 时代留下。</summary>
        private static string Legacy(int daysAgo, string alias = null)
        {
            string name = "Niniko_GO_" + Now.AddDays(-daysAgo)
                .ToString(LogVolume.DateFormat, CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(alias))
            {
                name += "_" + alias;
            }

            return name + LogVolume.Extension;
        }

        /// <summary>
        /// 一个候选文件。mtime 默认落在多开豁免窗口之外（一小时前），
        /// 这样"最近被写过"这条豁免不会在不相干的用例里悄悄生效。
        /// </summary>
        private static LogVolume.LogVolumeFile Entry(string name, long bytes = 1024, double mtimeMinutesAgo = 60)
        {
            return new LogVolume.LogVolumeFile(name, bytes, Now.AddMinutes(-mtimeMinutesAgo));
        }

        private static List<LogVolume.LogVolumeFile> Files(params LogVolume.LogVolumeFile[] entries)
        {
            return new List<LogVolume.LogVolumeFile>(entries);
        }

        /// <summary>三条规则各自撑到"不可能触发"，用例再把要验的那一条调回来。</summary>
        private static LogConfig Relaxed()
        {
            var cfg = new LogConfig();
            cfg.VolumeKeepCount = int.MaxValue;
            cfg.VolumeKeepDays = int.MaxValue;
            cfg.LocalMaxBytes = long.MaxValue;
            return cfg;
        }

        private static LogVolume.LogRetentionPlan Select(
            List<LogVolume.LogVolumeFile> files, LogConfig cfg, string currentName = null)
        {
            return LogVolume.SelectForDeletion(
                files, Now, cfg, currentName ?? CurrentName, ProductName);
        }

        // ================================================================================
        //  三条规则各自生效
        // ================================================================================

        /// <summary>
        /// 只有数量超限时：删到剩 <see cref="LogConfig.VolumeKeepCount"/> 个，**删的是最旧的**。
        /// <para>
        /// 用的是参数总表的默认值（60），不是测试自己写死的数字 ——
        /// 这条断言同时钉住"票里那个 60 确实是从 LogConfig 里取的"。
        /// </para>
        /// <para>当前卷不占名额：KeepCount 约束的是**历史卷**（见 LogConfig 上的注释）。</para>
        /// </summary>
        [Test]
        public void SelectForDeletion_DeletesTheOldestWhenCountExceedsKeepCount()
        {
            var cfg = Relaxed();
            cfg.VolumeKeepCount = new LogConfig().VolumeKeepCount;

            const int extra = 5;
            var files = new List<LogVolume.LogVolumeFile>();
            files.Add(Entry(CurrentName));
            for (int i = 0; i < cfg.VolumeKeepCount + extra; i++)
            {
                // 第 i 个卷是 i 天前的，于是 i 越大越旧。
                files.Add(Entry(Vol(i, i.ToString("x8"))));
            }

            LogVolume.LogRetentionPlan plan = Select(files, cfg);

            Assert.AreEqual(extra, plan.Delete.Count, "只该删掉超出 KeepCount 的那几个");
            for (int i = 0; i < extra; i++)
            {
                int daysAgo = cfg.VolumeKeepCount + i;
                CollectionAssert.Contains(plan.Delete, Vol(daysAgo, daysAgo.ToString("x8")), "删的必须是最旧的");
            }
        }

        /// <summary>
        /// 只有总字节超限时：删到 <see cref="LogConfig.LocalMaxBytes"/> 以下，**删的是最旧的**。
        /// <para>
        /// 10 个 10MB 的卷共 100MB，预算 60MB → 删掉最旧的 4 个正好回到 60MB。
        /// 删到"≤ 预算"就停，不多删一个。
        /// </para>
        /// </summary>
        [Test]
        public void SelectForDeletion_DeletesTheOldestUntilTotalBytesFitLocalMaxBytes()
        {
            var cfg = Relaxed();
            cfg.LocalMaxBytes = new LogConfig().LocalMaxBytes;

            long ten = 10L * 1024 * 1024;
            var files = new List<LogVolume.LogVolumeFile>();
            files.Add(Entry(CurrentName, ten));
            for (int i = 0; i < 10; i++)
            {
                files.Add(Entry(Vol(i, i.ToString("x8")), ten));
            }

            LogVolume.LogRetentionPlan plan = Select(files, cfg);

            Assert.AreEqual(4, plan.Delete.Count);
            for (int i = 6; i < 10; i++)
            {
                CollectionAssert.Contains(plan.Delete, Vol(i, i.ToString("x8")), "删的必须是最旧的");
            }
        }

        /// <summary>
        /// 只有天数超限时：删掉 <see cref="LogConfig.VolumeKeepDays"/> 天前的，保留期内的一个不删。
        /// <para>
        /// 同时钉住边界：**正好 7 天前的删，6 天前的留**（保留今天往回数 7 个自然日）。
        /// </para>
        /// </summary>
        [Test]
        public void SelectForDeletion_DeletesVolumesOlderThanKeepDaysAndKeepsTheRest()
        {
            var cfg = Relaxed();
            cfg.VolumeKeepDays = new LogConfig().VolumeKeepDays;

            var files = Files(
                Entry(CurrentName),
                Entry(Vol(1, "a0000001")),
                Entry(Vol(6, "a0000006")),
                Entry(Vol(7, "a0000007")),
                Entry(Vol(8, "a0000008")),
                Entry(Vol(30, "a0000030")));

            LogVolume.LogRetentionPlan plan = Select(files, cfg);

            CollectionAssert.AreEquivalent(
                new[] { Vol(7, "a0000007"), Vol(8, "a0000008"), Vol(30, "a0000030") }, plan.Delete);
        }

        /// <summary>
        /// 三条规则同时命中时**取并集、不重复删**。
        /// <para>
        /// A / B 被天数和数量两条同时选中（各选一次），C 再被字节预算补上 ——
        /// 结果必须是 3 个不同的文件，不能出现同一个名字两次
        /// （真去删第二次会得到一个 FileNotFoundException，然后被记成"删除失败"）。
        /// </para>
        /// </summary>
        [Test]
        public void SelectForDeletion_UnionsTheThreeRulesWithoutDuplicates()
        {
            var cfg = new LogConfig();
            cfg.VolumeKeepDays = 7;
            cfg.VolumeKeepCount = 2;
            cfg.LocalMaxBytes = 150;

            var files = Files(
                Entry(CurrentName, 1000),
                Entry(Vol(10, "aaaaaaaa"), 10),
                Entry(Vol(8, "bbbbbbbb"), 10),
                Entry(Vol(1, "dddddddd"), 100),
                Entry(Vol(0, "eeeeeeee"), 100));

            LogVolume.LogRetentionPlan plan = Select(files, cfg);

            CollectionAssert.AllItemsAreUnique(plan.Delete, "同一个文件不能被两条规则各删一次");
            CollectionAssert.AreEquivalent(
                new[] { Vol(10, "aaaaaaaa"), Vol(8, "bbbbbbbb"), Vol(1, "dddddddd") }, plan.Delete);
        }

        // ================================================================================
        //  当前会话的卷：在任何规则下都不被删
        // ================================================================================

        /// <summary>
        /// 数量超限时，当前卷不被删 —— 哪怕它是**最旧的那个**（数量规则删的正是最旧的）。
        /// </summary>
        [Test]
        public void SelectForDeletion_NeverDeletesTheCurrentVolume_CountRule()
        {
            var cfg = Relaxed();
            cfg.VolumeKeepCount = 1;

            // 当前卷刻意造成最旧的一个：不豁免的话它一定第一个被数量规则选中。
            string current = Vol(9, "cccccccc");
            var files = Files(
                Entry(current),
                Entry(Vol(2, "a0000002")),
                Entry(Vol(1, "a0000001")));

            LogVolume.LogRetentionPlan plan = Select(files, cfg, current);

            CollectionAssert.DoesNotContain(plan.Delete, current, "正在写的卷永远不能被删");
            CollectionAssert.AreEquivalent(new[] { Vol(2, "a0000002") }, plan.Delete);
        }

        /// <summary>
        /// 字节预算超限时，当前卷不被删 —— 哪怕它又旧又大，删了最省事。
        /// </summary>
        [Test]
        public void SelectForDeletion_NeverDeletesTheCurrentVolume_BytesRule()
        {
            var cfg = Relaxed();
            cfg.LocalMaxBytes = 100;

            string current = Vol(9, "cccccccc");
            var files = Files(
                Entry(current, 10000),
                Entry(Vol(1, "a0000001"), 60),
                Entry(Vol(0, "a0000000"), 60));

            LogVolume.LogRetentionPlan plan = Select(files, cfg, current);

            CollectionAssert.DoesNotContain(plan.Delete, current, "正在写的卷永远不能被删");
            CollectionAssert.AreEquivalent(new[] { Vol(1, "a0000001") }, plan.Delete);
        }

        /// <summary>
        /// 天数超限时，当前卷不被删 —— 哪怕它的日期已经落在保留期外。
        /// </summary>
        [Test]
        public void SelectForDeletion_NeverDeletesTheCurrentVolume_DaysRule()
        {
            var cfg = Relaxed();
            cfg.VolumeKeepDays = 7;

            string current = Vol(30, "cccccccc");
            var files = Files(
                Entry(current),
                Entry(Vol(8, "a0000008")),
                Entry(Vol(1, "a0000001")));

            LogVolume.LogRetentionPlan plan = Select(files, cfg, current);

            CollectionAssert.DoesNotContain(plan.Delete, current, "正在写的卷永远不能被删");
            CollectionAssert.AreEquivalent(new[] { Vol(8, "a0000008") }, plan.Delete);
        }

        /// <summary>
        /// **系统时间被回拨到过去**：当前卷的日期落在"7 天前"，它仍然不被删。
        ///
        /// <para>
        /// 这是当前卷豁免最要命的那个场景，也是它存在的理由（specs/0001）：
        /// 玩家把系统时间调乱之后，正在写的那个文件会在自己的清理规则眼里"过期"，
        /// 不豁免就会**当场把自己正在写的日志删掉**。
        /// </para>
        /// <para>
        /// 这里用的是**全默认配置**、且三条规则同时开着，因为回拨现场本来就是三条一起判的。
        /// </para>
        /// </summary>
        [Test]
        public void SelectForDeletion_KeepsTheCurrentVolumeWhenTheSystemClockWasRolledBack()
        {
            var cfg = new LogConfig();

            string current = Vol(10, "cccccccc");
            var files = Files(
                Entry(current, 30L * 1024 * 1024),
                Entry(Vol(9, "a0000009")),
                Entry(Vol(0, "a0000000")));

            LogVolume.LogRetentionPlan plan = Select(files, cfg, current);

            CollectionAssert.DoesNotContain(
                plan.Delete, current, "时钟被回拨也不能删正在写的卷 —— 那是丢日志，不是省空间");
            CollectionAssert.Contains(plan.Delete, Vol(9, "a0000009"), "别的过期卷照删不误");
        }

        // ================================================================================
        //  边角：空目录、只有当前卷、不认识的文件
        // ================================================================================

        /// <summary>目录为空时不报错，也不删任何东西。</summary>
        [Test]
        public void SelectForDeletion_ReturnsAnEmptyPlanForAnEmptyDirectory()
        {
            LogVolume.LogRetentionPlan plan = Select(Files(), new LogConfig());

            Assert.AreEqual(0, plan.Delete.Count);
        }

        /// <summary>输入为 null（目录扫描失败的降级路径）时不抛。</summary>
        [Test]
        public void SelectForDeletion_ReturnsAnEmptyPlanForNullInput()
        {
            LogVolume.LogRetentionPlan plan = null;

            Assert.DoesNotThrow(() =>
                plan = LogVolume.SelectForDeletion(null, Now, new LogConfig(), CurrentName, ProductName));
            Assert.AreEqual(0, plan.Delete.Count);
        }

        /// <summary>目录里只有一个卷、而且就是当前卷时不报错、不删。**第一次启动就是这个形状。**</summary>
        [Test]
        public void SelectForDeletion_KeepsTheOnlyVolumeWhenItIsTheCurrentOne()
        {
            var cfg = new LogConfig();
            cfg.VolumeKeepCount = 0;
            cfg.LocalMaxBytes = 0;
            cfg.VolumeKeepDays = 0;

            LogVolume.LogRetentionPlan plan = Select(Files(Entry(CurrentName)), cfg);

            Assert.AreEqual(0, plan.Delete.Count, "三条规则全部收紧到 0，当前卷依然不能被删");
        }

        /// <summary>
        /// 文件名不符合命名规则的一律**忽略**，绝不误删。
        ///
        /// <para>
        /// 日志目录是 persistentDataPath，别的程序、别的模块、以及日志系统自己的
        /// client_id 文件都住在里面。清理只认"清洗后的 productName + 合法日期"这个开头 ——
        /// 认不出来就当它跟我们无关，这是唯一安全的默认。
        /// </para>
        /// </summary>
        [Test]
        public void SelectForDeletion_IgnoresFilesThatDoNotMatchTheNamingRules()
        {
            var cfg = new LogConfig();
            cfg.VolumeKeepCount = 0;
            cfg.LocalMaxBytes = 0;
            cfg.VolumeKeepDays = 0;

            var files = Files(
                Entry(CurrentName),
                Entry("Player.log"),                          // Unity 自己的
                Entry("unity_crash_2020_01_01.log"),          // 别的程序留下的
                Entry("log_client_id.txt"),                   // 日志系统自己的，不是 .log
                Entry("Niniko_GO.log"),                       // 有产品名但没有日期
                Entry("Niniko_GO_2026_13_45_a1b2c3d4.log"),   // 日期段不是合法日期
                Entry("Niniko_GO_2026_09_08x.log"),           // 日期后面直接跟字符
                Entry("Niniko GO_2026_09_08_a1b2c3d4.log"),   // 产品名没清洗过：不是我们写的
                Entry("OtherGame_2020_01_01_aabbccdd.log"));  // 另一个游戏的卷

            LogVolume.LogRetentionPlan plan = Select(files, cfg);

            Assert.AreEqual(0, plan.Delete.Count,
                "认不出来的文件一律不碰，哪怕三条规则都收紧到 0：" + string.Join(", ", plan.Delete.ToArray()));
        }

        // ================================================================================
        //  旧格式的一次性迁移删除（specs/0001「旧格式日志的迁移」）
        // ================================================================================

        /// <summary>
        /// 旧格式（无 alias）<c>{product}_{date}.log</c> 一次性删掉，**哪怕是今天的**。
        ///
        /// <para>
        /// 玩家设备上这些文件是从装机那天起攒下来的：旧 deleteYesterday 用
        /// <c>StartsWith(Application.productName)</c> 匹配，而写文件名时用的是
        /// <c>productName.Replace(' ', '_')</c>，productName 是 "Niniko GO" 含空格 ——
        /// 匹配永远为 false，**一个都没删过**。它们的命名规则和新卷不同，
        /// 靠新卷的保留逻辑自然淘汰做不到，只能启动时一次性识别并删除。
        /// </para>
        /// </summary>
        [Test]
        public void SelectForDeletion_DeletesLegacyFilesEvenWhenTheyAreFromToday()
        {
            var cfg = Relaxed();

            var files = Files(
                Entry(CurrentName),
                Entry(Legacy(0)),
                Entry(Legacy(3)),
                Entry(Legacy(400)));

            LogVolume.LogRetentionPlan plan = Select(files, cfg);

            CollectionAssert.AreEquivalent(new[] { Legacy(0), Legacy(3), Legacy(400) }, plan.Delete);
            Assert.AreEqual(3, plan.LegacyCount, "这三个都该记在旧格式迁移的账上");
        }

        /// <summary>
        /// 旧格式（带 alias）<c>{product}_{date}_{alias}.log</c> 同样删。
        /// <para>
        /// alias 是旧代码取的登录名前 10 个字符（<c>Main.cs</c>: <c>Name?.Substring(0, 10)</c>），
        /// 内容不可预测 —— 所以判定规则不能去认 alias 长什么样，只能是
        /// "日期后面那一段**不是** 8 位十六进制的 traceId"。
        /// </para>
        /// </summary>
        [Test]
        public void SelectForDeletion_DeletesLegacyFilesThatCarryAnAlias()
        {
            var cfg = Relaxed();

            var files = Files(
                Entry(CurrentName),
                Entry(Legacy(2, "zhangsan01")),
                Entry(Legacy(2, "1234567890")),
                Entry(Legacy(2, "guest")));

            LogVolume.LogRetentionPlan plan = Select(files, cfg);

            CollectionAssert.AreEquivalent(
                new[] { Legacy(2, "zhangsan01"), Legacy(2, "1234567890"), Legacy(2, "guest") }, plan.Delete);
        }

        /// <summary>
        /// ⚠️ 迁移**不能误伤新卷**：日期后面跟着 8 位十六进制的就是新卷，
        /// 保留期内的一个都不许删。
        /// <para>
        /// 这是整条迁移规则最危险的方向 —— 认宽了就会把今天刚写完的会话日志整批删光。
        /// </para>
        /// </summary>
        [Test]
        public void SelectForDeletion_DoesNotTreatRecentVolumesAsLegacy()
        {
            var cfg = new LogConfig();

            var files = Files(
                Entry(CurrentName),
                Entry(Vol(0, "0123abcd")),
                Entry(Vol(1, "ABCDEF01")),   // 大小写都算十六进制
                Entry(Legacy(0)));

            LogVolume.LogRetentionPlan plan = Select(files, cfg);

            CollectionAssert.AreEquivalent(new[] { Legacy(0) }, plan.Delete);
            Assert.AreEqual(1, plan.LegacyCount);
        }

        // ================================================================================
        //  多开豁免 与 "年龄看文件名不看 mtime"
        // ================================================================================

        /// <summary>
        /// **多开豁免**：mtime 在 <see cref="LogVolume.ActiveVolumeGraceSec"/> 之内的非己卷跳过。
        /// <para>
        /// PC 上开两个客户端对练时，另一个实例正在写的卷如果被删掉，对方的日志会凭空消失。
        /// "最近被写过"是唯一不用跨进程锁就能拿到的证据。
        /// </para>
        /// </summary>
        [Test]
        public void SelectForDeletion_SkipsVolumesAnotherInstanceMightBeWriting()
        {
            var cfg = new LogConfig();
            string busy = Vol(30, "b0000030");

            var files = Files(
                Entry(CurrentName),
                Entry(busy, 1024, 1),                    // 1 分钟前还在写
                Entry(Vol(30, "a0000030"), 1024, 60));   // 一小时没动静

            LogVolume.LogRetentionPlan plan = Select(files, cfg);

            CollectionAssert.DoesNotContain(plan.Delete, busy, "可能是另一个实例正在写它");
            CollectionAssert.Contains(plan.Delete, Vol(30, "a0000030"), "没人在写的过期卷照删");
        }

        /// <summary>
        /// 过了豁免窗口（这里 6 分钟 > 5 分钟）就不再受保护 ——
        /// 豁免只是"可能有人在用"，不是"永久保命符"。
        /// </summary>
        [Test]
        public void SelectForDeletion_DeletesVolumesOnceTheActiveGraceHasPassed()
        {
            var cfg = new LogConfig();
            string idle = Vol(30, "b0000030");

            var files = Files(
                Entry(CurrentName),
                Entry(idle, 1024, LogVolume.ActiveVolumeGraceSec / 60.0 + 1));

            LogVolume.LogRetentionPlan plan = Select(files, cfg);

            CollectionAssert.Contains(plan.Delete, idle);
        }

        /// <summary>
        /// 旧格式文件也吃多开豁免：票 11 之前新旧两套并存，
        /// 对面那个客户端（或本进程里还没下线的 LogWrite）此刻可能正在往它里面写。
        /// </summary>
        [Test]
        public void SelectForDeletion_SkipsLegacyFilesThatAreStillBeingWritten()
        {
            var cfg = new LogConfig();

            var files = Files(
                Entry(CurrentName),
                Entry(Legacy(0, "zhangsan01"), 1024, 1),
                Entry(Legacy(1, "zhangsan01"), 1024, 60));

            LogVolume.LogRetentionPlan plan = Select(files, cfg);

            CollectionAssert.AreEquivalent(new[] { Legacy(1, "zhangsan01") }, plan.Delete);
        }

        /// <summary>
        /// mtime 落在**未来**（系统时间被回拨，或文件是从别的机器拷过来的）时一律不删。
        /// <para>这种文件我们对它一无所知，"不删"是唯一安全的方向。</para>
        /// </summary>
        [Test]
        public void SelectForDeletion_SkipsFilesWhoseTimestampIsInTheFuture()
        {
            var cfg = new LogConfig();
            string weird = Vol(30, "b0000030");

            var files = Files(
                Entry(CurrentName),
                Entry(weird, 1024, -60 * 24));

            LogVolume.LogRetentionPlan plan = Select(files, cfg);

            CollectionAssert.DoesNotContain(plan.Delete, weird);
        }

        /// <summary>
        /// ⚠️ **年龄从文件名解析，不看 mtime。**
        /// <para>
        /// 一个今天刚写的卷，如果 mtime 被文件拷贝或系统时间变化污染成 30 天前，
        /// 它**仍然不该**被天数规则删掉。这两个用途（年龄 / 多开豁免）都用得到时间，
        /// 但依据不同，不能合并 —— 合并的那一刻就会开始删今天的日志。
        /// </para>
        /// </summary>
        [Test]
        public void SelectForDeletion_JudgesAgeByFileNameNotByLastWriteTime()
        {
            var cfg = Relaxed();
            cfg.VolumeKeepDays = 7;

            string fresh = Vol(0, "a0000000");
            string stale = Vol(30, "a0000030");

            var files = Files(
                Entry(CurrentName),
                Entry(fresh, 1024, 60 * 24 * 30),   // 名字是今天的，mtime 却是 30 天前
                Entry(stale, 1024, 6));             // 名字是 30 天前的，mtime 是 6 分钟前

            LogVolume.LogRetentionPlan plan = Select(files, cfg);

            CollectionAssert.DoesNotContain(plan.Delete, fresh, "今天的卷不能因为 mtime 老就被删");
            CollectionAssert.Contains(plan.Delete, stale, "30 天前的卷不能因为 mtime 新就免死");
        }

        /// <summary>
        /// 卷名的写与读都必须走 InvariantCulture：设备区域挂着**非公历日历**（泰语佛历、
        /// 部分阿拉伯语区的回历）时，<c>ToString("yyyy_MM_dd")</c> 会写出 2569 这样的年份。
        ///
        /// <para>
        /// 写和读用不同的日历，就是"写下划线、读原名"那个从未生效的 deleteYesterday 换个形状
        /// 再来一遍：卷名的日期全落在未来 → 永远不过期 → 无限堆积。
        /// 这条用例把两边一起钉住 —— 在泰语区造名字，回到默认区域仍然要能判出它已过期。
        /// </para>
        /// </summary>
        [Test]
        public void SelectForDeletion_ParsesVolumeNamesWrittenUnderANonGregorianCulture()
        {
            CultureInfo thai;
            try
            {
                thai = new CultureInfo("th-TH");
            }
            catch (CultureNotFoundException)
            {
                Assert.Ignore("运行环境没有 th-TH 的区域数据，跳过");
                return;
            }

            CultureInfo previous = Thread.CurrentThread.CurrentCulture;
            string name;
            try
            {
                Thread.CurrentThread.CurrentCulture = thai;
                name = LogVolume.BuildFileName(ProductName, Now.AddDays(-30), "a0000030");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }

            Assert.AreEqual(Vol(30, "a0000030"), name, "卷名不能随设备区域的日历变形");

            LogVolume.LogRetentionPlan plan = Select(Files(Entry(CurrentName), Entry(name)), new LogConfig());

            CollectionAssert.Contains(plan.Delete, name, "认不出来就等于永不过期，那正是要修的 bug");
        }

        // ================================================================================
        //  IO 层：CleanupOnStartup 照着计划删
        // ================================================================================

        private string m_Dir;

        [SetUp]
        public void SetUp()
        {
            m_Dir = Path.Combine(Path.GetTempPath(), "UmbreeyLogRetentionTests", Guid.NewGuid().ToString("N"));
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

        /// <summary>
        /// 造一个候选文件。mtime 必须显式往前推：刚创建的文件落在多开豁免窗口里，
        /// 不推的话一个都删不掉，用例会得到一个"看起来通过了"的假象。
        /// </summary>
        private string MakeFile(string name, int bytes = 16, double mtimeMinutesAgo = 60)
        {
            string path = Path.Combine(m_Dir, name);
            File.WriteAllBytes(path, new byte[bytes]);
            File.SetLastWriteTime(path, DateTime.Now.AddMinutes(-mtimeMinutesAgo));
            return path;
        }

        private static string RealVol(int daysAgo, string trace8)
        {
            return LogVolume.BuildFileName(ProductName, DateTime.Now.AddDays(-daysAgo), trace8);
        }

        /// <summary>
        /// 端到端：启动清理删掉过期卷与旧格式文件，**当前卷（句柄还开着）安然无恙**，
        /// 别人的文件一根汗毛都不动。
        /// </summary>
        [Test]
        public void CleanupOnStartup_DeletesExpiredAndLegacyFilesButKeepsTheOpenCurrentVolume()
        {
            string expired = MakeFile(RealVol(30, "a0000030"));
            string legacy = MakeFile(
                "Niniko_GO_" + DateTime.Now.ToString(LogVolume.DateFormat, CultureInfo.InvariantCulture)
                + LogVolume.Extension);
            string foreign = MakeFile("Player.log");
            string recent = MakeFile(RealVol(1, "a0000001"));

            var volume = new LogVolume(new LogConfig(), m_Dir, ProductName, "cccccccc-1111-2222");
            volume.Open();
            volume.CleanupOnStartup();

            Assert.IsFalse(File.Exists(expired), "7 天前的卷该被删");
            Assert.IsFalse(File.Exists(legacy), "旧格式文件该被一次性迁移删除");
            Assert.IsTrue(File.Exists(foreign), "别的程序留下的 .log 不许碰");
            Assert.IsTrue(File.Exists(recent), "保留期内的卷不许碰");
            Assert.IsTrue(File.Exists(volume.FilePath), "当前卷永远不许碰");

            // 删完之后当前卷的句柄还必须是好的 —— 清理不该碰它，连"以为它没了"都不行。
            volume.Write(new List<LogRecord> { LogRecord.Capture(0L, UnityEngine.LogType.Log, "still alive", "", 0, false) });
            volume.Close();
            StringAssert.Contains("still alive", File.ReadAllText(volume.FilePath));
        }

        /// <summary>清理**只做一次**：第二次调用是空转，不再扫目录（specs/0001）。</summary>
        [Test]
        public void CleanupOnStartup_RunsOnlyOnce()
        {
            var volume = new LogVolume(new LogConfig(), m_Dir, ProductName, "cccccccc-1111-2222");
            volume.Open();
            volume.CleanupOnStartup();

            // 第一次清理之后才出现的过期卷：如果第二次调用真的又扫了一遍目录，它会被删掉。
            string appeared = MakeFile(RealVol(30, "a0000030"));
            volume.CleanupOnStartup();
            volume.Close();

            Assert.IsTrue(File.Exists(appeared), "启动清理只在启动时做一次，不该被重复触发");
        }

        /// <summary>
        /// 删除失败（文件被占用等）**不抛异常、不中断启动**，剩下的照删。
        /// <para>
        /// 日志系统把宿主的启动搞挂，是比"多留几个日志文件"严重得多的事故。
        /// 失败会走 <c>Debug.LogError</c> 记一条 Internal 日志（全局硬约束 6），
        /// 所以这里放行失败消息。
        /// </para>
        /// <para>
        /// 只断言"不抛 + 别的照删"：占用中的文件能不能删掉是**平台行为**
        /// （Windows 拦、POSIX 放行），断言它就把用例绑死在某个平台上了。
        /// </para>
        /// </summary>
        [Test]
        public void CleanupOnStartup_SurvivesADeletionFailureAndKeepsGoing()
        {
            LogAssert.ignoreFailingMessages = true;

            string locked = MakeFile(RealVol(31, "a0000031"));
            string other = MakeFile(RealVol(30, "a0000030"));

            var volume = new LogVolume(new LogConfig(), m_Dir, ProductName, "cccccccc-1111-2222");
            volume.Open();

            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.DoesNotThrow(() => volume.CleanupOnStartup());
            }

            volume.Close();

            Assert.IsFalse(File.Exists(other), "一个文件删不掉不该拖累其它文件");
        }

        /// <summary>目录里除了当前卷什么都没有时不报错 —— 装机后第一次启动就是这个形状。</summary>
        [Test]
        public void CleanupOnStartup_IsSafeOnAFreshInstall()
        {
            var volume = new LogVolume(new LogConfig(), m_Dir, ProductName, "cccccccc-1111-2222");
            volume.Open();

            Assert.DoesNotThrow(() => volume.CleanupOnStartup());

            volume.Close();
            Assert.IsTrue(File.Exists(volume.FilePath));
        }
    }
}
