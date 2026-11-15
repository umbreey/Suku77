using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Umbreey
{
    public partial class UIManager
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // UI 开关耗时埋点（dev-only）：量化 SetViewVisible 里 3 趟 GetComponentsInChildren 的真实成本。
        // 约束：release 下整段不编译；对外 API 在 release 保留空实现（签名消失会让热更 dll 编译失败）；
        // 计时用 Stopwatch.GetTimestamp()（无分配）；只用引用类型泛型，规避 IL2CPP AOT 裁剪。

        private const int kPerfMaxNames = 64;

        private class PerfStat
        {
            public int count;
            public double totalMs;
            public double maxMs;
            public long totalGc;
            public double canvasMs;   // GetComponentsInChildren<Canvas>
            public double raycastMs;  // GetComponentsInChildren<GraphicRaycaster>
            public double fetchMs;    // GetComponentsInChildren<IUIVisibleNotify>
            public double loopMs;     // 逐个 SetVisible 的通知循环
            public double applyCanvasMs;  // 禁用/启用各 Canvas.enabled 的 for 循环（含 setter 同步开销）
            public double applyRaycastMs; // 禁用/启用各 GraphicRaycaster.enabled 的 for 循环
            public long hits;         // IUIVisibleNotify 命中累计数
            public int maxHits;       // 单次最大命中数
            public int nodes;         // 最近一次子树节点数（需开启 nodes 模式）
        }

        private static bool s_PerfEnabled;
        private static bool s_PerfNodes;
        private static readonly Dictionary<string, PerfStat> s_PerfOpen = new Dictionary<string, PerfStat>();
        private static readonly Dictionary<string, PerfStat> s_PerfClose = new Dictionary<string, PerfStat>();
        private static readonly List<string> s_PerfKeys = new List<string>();

        // A/B 对比模式：开启后在 canvas-toggle 测量之外，额外跑一遍 SetActive 路径探针，
        // 用 B 桶记录"若改用 SetActive 隐藏/显示该 UI"的耗时，与 A 桶(canvas-toggle)相减即为省掉的级联开销。
        private static bool s_PerfAB;
        private static readonly Dictionary<string, PerfStat> s_PerfOpenB = new Dictionary<string, PerfStat>();
        private static readonly Dictionary<string, PerfStat> s_PerfCloseB = new Dictionary<string, PerfStat>();

        // 采样栈：支持 SetViewVisible 嵌套（关闭父 UI 时同步关闭子 UIViewSub、或 OnVisibleChange 订阅者关闭其它 UI）。
        // 每层独立记到自己的 PerfStat：外层 other = 本层未覆盖的工作，被嵌套的内层作为独立行出现，不污染外层归属。
        private class PerfFrame
        {
            public PerfStat stat;
            public long t0;
            public long gc0;
            public double canvas;
            public double raycast;
            public double fetch;
            public double loop;
            public double applyCanvas;
            public double applyRaycast;
            public int hits;
            public Transform root;
        }
        private static readonly List<PerfFrame> s_PerfStack = new List<PerfFrame>();

        public static void SetVisiblePerf(bool on, bool countNodes = false)
        {
            s_PerfEnabled = on;
            s_PerfNodes = on && countNodes;
            if (on) return;
            s_PerfStack.Clear();
        }

        public static void SetVisiblePerfAB(bool on)
        {
            s_PerfAB = on;
            if (!on) { s_PerfOpenB.Clear(); s_PerfCloseB.Clear(); }
        }

        public static void ResetVisiblePerf()
        {
            s_PerfOpen.Clear();
            s_PerfClose.Clear();
            s_PerfOpenB.Clear();
            s_PerfCloseB.Clear();
            s_PerfStack.Clear();
        }

        /// <summary>
        /// 追加（不 Clear）到调用方 StringBuilder，便于和 UI 堆栈等调试信息拼在同一段文本里。
        /// 未开启统计时什么都不追加。
        /// </summary>
        public static void DumpVisiblePerf(StringBuilder sb)
        {
            if (!s_PerfEnabled || sb == null) return;
            sb.AppendLine();
            sb.AppendLine("[UIPerf] UI 开关耗时（A=canvas-toggle 路径）");
            AppendSection(sb, "关闭", s_PerfClose);
            AppendSection(sb, "打开", s_PerfOpen);
            if (s_PerfCloseB.Count > 0 || s_PerfOpenB.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("[UIPerf] A/B 对比：canvas-toggle(A) 相对 SetActive(B) 省掉的耗时（save=B-A，正值=省下）");
                AppendComparison(sb, "关闭", s_PerfClose, s_PerfCloseB);
                AppendComparison(sb, "打开", s_PerfOpen, s_PerfOpenB);
            }
        }

        // ---------------- 采样点：由 UIManager.cs 调用 ----------------

        private static long VisiblePerfNow()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp();
        }

        private static double VisiblePerfMs(long t0)
        {
            return (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 /
                   System.Diagnostics.Stopwatch.Frequency;
        }

        private static double VisiblePerfMsDelta(long deltaTicks)
        {
            return deltaTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        // A/B 对比探针：在 canvas-toggle(A) 测量之后额外跑一遍 SetActive(false)+SetActive(true)，
        // 测出"若改用 SetActive 隐藏/显示该 UI"的耗时（含整树 MonoBehaviour 的 OnDisable/OnEnable 级联），
        // 用 B 桶记录，与 A 相减即为本方案省掉的级联开销。
        // 侵入式：会真实触发整树 OnDisable/OnEnable，仅用于 dev 评测，勿在正式流程长期开启。
        // 探针结束后 UI 回落到与 A 相同的可见性状态（canvas-toggle 隐藏态下 GO 仍 active、仅 Canvas 被禁用，
        // SetActive(false)→(true) 不改变 Canvas.enabled，故末尾仍保持隐藏）。
        private static void VisiblePerfRunB(Entity entity, bool visible, GameObject go)
        {
            long gc0 = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
            long t0 = VisiblePerfNow();
            go.SetActive(false);              // B.close：SetActive 隐藏
            long t1 = VisiblePerfNow();
            long gc1 = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
            go.SetActive(true);               // 还原 GO active（Canvas.enabled 不受影响，仍保持隐藏态）
            long t2 = VisiblePerfNow();
            long gc2 = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
            RecordB(entity.name, false, VisiblePerfMsDelta(t1 - t0), gc1 - gc0);
            RecordB(entity.name, true, VisiblePerfMsDelta(t2 - t1), gc2 - gc1);
        }

        private static void RecordB(string name, bool open, double ms, long gc)
        {
            var dict = open ? s_PerfOpenB : s_PerfCloseB;
            PerfStat s;
            if (!dict.TryGetValue(name, out s))
            {
                if (dict.Count >= kPerfMaxNames) return; // 防止 Dictionary 无限增长
                s = new PerfStat();
                dict[name] = s;
            }
            s.count++;
            s.totalMs += ms;
            if (ms > s.maxMs) s.maxMs = ms;
            s.totalGc += gc;
        }

        private static void VisiblePerfBegin(string name, bool open, Transform root)
        {
            if (!s_PerfEnabled) return;
            var frame = new PerfFrame();
            frame.stat = GetStat(name, open);
            frame.t0 = VisiblePerfNow();
            frame.gc0 = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
            frame.canvas = 0;
            frame.raycast = 0;
            frame.fetch = 0;
            frame.loop = 0;
            frame.applyCanvas = 0;
            frame.applyRaycast = 0;
            frame.hits = 0;
            frame.root = root;
            s_PerfStack.Add(frame);
        }

        private static void VisiblePerfEnd()
        {
            if (!s_PerfEnabled || s_PerfStack.Count == 0) return;
            var frame = s_PerfStack[s_PerfStack.Count - 1];
            s_PerfStack.RemoveAt(s_PerfStack.Count - 1);
            var s = frame.stat;
            if (s == null) return;
            // 节点数统计放在计时结束前，使其开销计入总耗时（nodes 模式下总数才反映真实 wall time）
            if (s_PerfNodes && frame.root) s.nodes = CountNodes(frame.root);
            double ms = VisiblePerfMs(frame.t0);
            s.count++;
            s.totalMs += ms;
            if (ms > s.maxMs) s.maxMs = ms;
            s.totalGc += UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() - frame.gc0;
            s.canvasMs += frame.canvas;
            s.raycastMs += frame.raycast;
            s.fetchMs += frame.fetch;
            s.loopMs += frame.loop;
            s.applyCanvasMs += frame.applyCanvas;
            s.applyRaycastMs += frame.applyRaycast;
            s.hits += frame.hits;
            if (frame.hits > s.maxHits) s.maxHits = frame.hits;
        }

        private static void VisiblePerfAccCanvas(long t0)
        {
            if (s_PerfStack.Count == 0) return;
            s_PerfStack[s_PerfStack.Count - 1].canvas += VisiblePerfMs(t0);
        }

        private static void VisiblePerfAccRaycast(long t0)
        {
            if (s_PerfStack.Count == 0) return;
            s_PerfStack[s_PerfStack.Count - 1].raycast += VisiblePerfMs(t0);
        }

        private static void VisiblePerfAccFetch(long t0, int hits)
        {
            if (s_PerfStack.Count == 0) return;
            var f = s_PerfStack[s_PerfStack.Count - 1];
            f.fetch += VisiblePerfMs(t0);
            f.hits += hits;
        }

        private static void VisiblePerfAccLoop(long t0)
        {
            if (s_PerfStack.Count == 0) return;
            s_PerfStack[s_PerfStack.Count - 1].loop += VisiblePerfMs(t0);
        }

        private static void VisiblePerfAccApplyCanvas(long t0)
        {
            if (s_PerfStack.Count == 0) return;
            s_PerfStack[s_PerfStack.Count - 1].applyCanvas += VisiblePerfMs(t0);
        }

        private static void VisiblePerfAccApplyRaycast(long t0)
        {
            if (s_PerfStack.Count == 0) return;
            s_PerfStack[s_PerfStack.Count - 1].applyRaycast += VisiblePerfMs(t0);
        }

        // ---------------- 内部工具 ----------------

        private static PerfStat GetStat(string name, bool open)
        {
            var dict = open ? s_PerfOpen : s_PerfClose;
            PerfStat s;
            if (dict.TryGetValue(name, out s)) return s;
            if (dict.Count >= kPerfMaxNames) return null; // 防止 Dictionary 无限增长
            s = new PerfStat();
            dict[name] = s;
            return s;
        }

        private static int CountNodes(Transform t)
        {
            int n = 1;
            int c = t.childCount;
            for (int i = 0; i < c; i++) n += CountNodes(t.GetChild(i));
            return n;
        }

        private static void AppendSection(StringBuilder sb, string title, Dictionary<string, PerfStat> dict)
        {
            if (dict.Count == 0) return;
            s_PerfKeys.Clear();
            s_PerfKeys.AddRange(dict.Keys);
            s_PerfKeys.Sort((a, b) => dict[b].totalMs.CompareTo(dict[a].totalMs));
            sb.AppendLine($"  -- {title} --");
            for (int i = 0; i < s_PerfKeys.Count; i++)
            {
                var s = dict[s_PerfKeys[i]];
                double c = s.count > 0 ? s.count : 1;
                double avg = s.totalMs / c;
                double avgCanvas = s.canvasMs / c;
                double avgRaycast = s.raycastMs / c;
                double avgFetch = s.fetchMs / c;
                double avgLoop = s.loopMs / c;
                double avgApplyCanvas = s.applyCanvasMs / c;
                double avgApplyRaycast = s.applyRaycastMs / c;
                double avgGc = (s.totalGc / 1024.0) / c;
                sb.Append("  ").Append(s_PerfKeys[i]).Append("  n=").Append(s.count)
                  .Append("  avg=").Append(avg.ToString("F3")).Append("ms")
                  .Append("  max=").Append(s.maxMs.ToString("F3")).Append("ms")
                  .Append("  gc=").Append(avgGc.ToString("F1")).Append("KB");
                sb.Append("  | canvas=").Append(avgCanvas.ToString("F3"))
                  .Append("  raycast=").Append(avgRaycast.ToString("F3"))
                  .Append("  fetch=").Append(avgFetch.ToString("F3"))
                  .Append("  loop=").Append(avgLoop.ToString("F3"))
                  .Append("  applyC=").Append(avgApplyCanvas.ToString("F3"))
                  .Append("  applyR=").Append(avgApplyRaycast.ToString("F3"));
                double other = Math.Max(0, avg - avgCanvas - avgRaycast - avgFetch - avgLoop - avgApplyCanvas - avgApplyRaycast);
                sb.Append("  other=").Append(other.ToString("F3"));
                sb.Append("  | hits=").Append(s.hits).Append("(peak ").Append(s.maxHits).Append(")");
                if (s_PerfNodes) sb.Append("  nodes=").Append(s.nodes);
                sb.AppendLine();
            }
        }

        private static void AppendComparison(StringBuilder sb, string title,
            Dictionary<string, PerfStat> a, Dictionary<string, PerfStat> b)
        {
            if (a.Count == 0 && b.Count == 0) return;
            var names = new List<string>();
            foreach (var k in a.Keys) if (!names.Contains(k)) names.Add(k);
            foreach (var k in b.Keys) if (!names.Contains(k)) names.Add(k);
            sb.AppendLine($"  -- {title} --");
            for (int i = 0; i < names.Count; i++)
            {
                var name = names[i];
                double aAvg = a.ContainsKey(name) ? a[name].totalMs / Math.Max(1, a[name].count) : 0;
                double bAvg = b.ContainsKey(name) ? b[name].totalMs / Math.Max(1, b[name].count) : 0;
                double save = bAvg - aAvg;
                double pct = bAvg > 0 ? save / bAvg * 100.0 : 0;
                sb.Append("  ").Append(name).Append("  A=").Append(aAvg.ToString("F3")).Append("ms")
                  .Append("  B=").Append(bAvg.ToString("F3")).Append("ms")
                  .Append("  save=").Append(save.ToString("F3")).Append("ms")
                  .Append("(").Append(pct.ToString("F0")).Append("%")
                  .Append(save >= 0 ? " 省下)" : " 反而更慢)").AppendLine();
            }
        }
#else
        // ---- release：空实现。签名必须保留，热更程序集（GmUtils）会无条件调用 ----
        public static void SetVisiblePerf(bool on, bool countNodes = false) { }

        public static void SetVisiblePerfAB(bool on) { }

        public static void ResetVisiblePerf() { }

        public static void DumpVisiblePerf(StringBuilder sb) { }
#endif
    }
}
