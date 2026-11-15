using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Umbreey.Editor
{
    /// <summary>
    /// UIGen 的逆操作：把 Gen 形态的 prefab 还原成可美术编辑的原生形态。
    ///
    /// 三种使用方式：
    ///   1) 在 Project 窗口选中 prefab / 文件夹 -> 「Umbreey/UIRevert」整 prefab 还原并就地覆盖保存。
    ///   2) 在 Prefab Mode 中打开某个 prefab 时，直接「Umbreey/UIRevert」即可还原当前打开的 prefab 本体（不再整轮跳过）。
    ///   3) 在 Hierarchy / Prefab Mode 选中若干 UI 节点 -> 「Umbreey/UIRevert Node」只还原这些节点下的子树。
    ///
    /// 资产流程参考 TravelStickerGen：Instantiate -> 改 -> SaveAsPrefabAssetAndConnect -> DestroyImmediate；
    /// Prefab Mode / 节点流程为就地修改后 stage.Save() 或落在场景。
    /// 不产生任何本地文件（无备份、无日志），所有结果只输出到 Console。
    /// </summary>
    public static partial class UIRevert
    {
        const string kMenu = "Umbreey/UIRevert";
        const string kMenuNode = "Umbreey/UIRevert Node";

        [MenuItem(kMenu, false, 120)]
        static void MenuRevert()
        {
            Run();
        }

        [MenuItem(kMenu, true)]
        static bool MenuRevertValidate()
        {
            // 打开着 Prefab（Prefab Mode）时也能直接还原当前 prefab 本体
            if (PrefabStageUtility.GetCurrentPrefabStage() != null) return true;
            return CollectTargets().Count > 0;
        }

        [MenuItem(kMenuNode, false, 121)]
        static void MenuRevertNode()
        {
            RevertSelectedNodes();
        }

        [MenuItem(kMenuNode, true)]
        static bool MenuRevertNodeValidate()
        {
            return Selection.gameObjects.Length > 0;
        }

        #region 选区

        static List<string> CollectTargets()
        {
            var list = new List<string>();
            foreach (var o in Selection.objects)
            {
                if (o == null) continue;
                string p = AssetDatabase.GetAssetPath(o);
                if (string.IsNullOrEmpty(p)) continue;

                if (AssetDatabase.IsValidFolder(p))
                {
                    foreach (var guid in AssetDatabase.FindAssets("t:GameObject", new[] { p }))
                    {
                        string fp = AssetDatabase.GUIDToAssetPath(guid);
                        if (fp.EndsWith(".prefab")) list.Add(fp);
                    }
                }
                else if (p.EndsWith(".prefab"))
                {
                    list.Add(p);
                }
            }

            return list.Distinct().OrderBy(x => x).ToList();
        }

        #endregion

        #region 主流程

        static void Run()
        {
            var report = new Report();
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            var targets = CollectTargets();

            // 当前正处于 Prefab Mode 时，直接还原「正在打开的 prefab 本体」（就地改 stage 内容并保存），
            // 不再因为资产被占用而整轮跳过。
            if (stage != null)
            {
                if (targets.Contains(stage.assetPath))
                    targets.Remove(stage.assetPath); // 该资产走 stage 流程，不走资产实例化流程
                RevertOpenStage(stage, report);
            }

            // 其余选中的 prefab 资产（Project 窗口里的）按原逻辑实例化后还原并覆盖保存
            if (targets.Count > 0)
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    EditorUtility.DisplayProgressBar("UIRevert", targets[i], (float)i / targets.Count);
                    RevertOne(targets[i], report);
                }
            }

            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            report.Log();
        }

        static void RevertOne(string path, Report report)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null)
            {
                report.Failed.Add($"{path}（无法加载）");
                return;
            }

            // 完全没有 UIGen 痕迹的 prefab 直接跳过，避免误伤普通资产
            if (!HasGenMarkers(asset))
            {
                report.Skipped.Add(path);
                return;
            }

            GameObject go = null;
            try
            {
                go = Object.Instantiate(asset);
                go.name = asset.name;

                RevertRoot(go, report);

                PrefabUtility.SaveAsPrefabAssetAndConnect(go, path, InteractionMode.AutomatedAction);
                report.Saved.Add(path);
            }
            catch (Exception e)
            {
                report.Failed.Add($"{path}（{e.Message}）");
                Debug.LogError($"[UIRevert] 还原异常：{path}\n{e}");
            }
            finally
            {
                if (go != null)
                    Object.DestroyImmediate(go);
            }
        }

        /// <summary>该 prefab 是否带有 UIGen 的痕迹。</summary>
        static bool HasGenMarkers(GameObject asset)
        {
            foreach (var c in asset.GetComponentsInChildren<Component>(true))
            {
                if (c is UIImage || c is UIRawImage || c is UIText || c is UITextMeshPro) return true;
                if (c is UIBind || c is UIBindItem) return true;
                if (c is UILang || c is UILangLibrary) return true;
            }

            return false;
        }

        #endregion

        #region Prefab Mode / 节点还原

        /// <summary>
        /// 当前正处于 Prefab Mode 时：直接还原「正在打开的 prefab 本体」。
        /// 不再因为资产被占用而整轮跳过 —— 就地修改 stage 内容后 stage.Save() 写回资产。
        /// </summary>
        static void RevertOpenStage(PrefabStage stage, Report report)
        {
            var root = stage.prefabContentsRoot;
            if (!HasGenMarkers(root))
            {
                report.Skipped.Add(stage.assetPath);
                return;
            }

            try
            {
                RevertRoot(root, report);
                PrefabUtility.SaveAsPrefabAssetAndConnect(root, stage.assetPath, InteractionMode.AutomatedAction);
                report.Saved.Add(stage.assetPath);
            }
            catch (Exception e)
            {
                report.Failed.Add($"{stage.assetPath}（{e.Message}）");
                Debug.LogError($"[UIRevert] 还原异常：{stage.assetPath}\n{e}");
            }
        }

        /// <summary>
        /// 还原选中的单个/多个 UI 节点（其下整棵子树），在 Hierarchy / Prefab Mode 中就地操作。
        /// 引用快照范围取节点的「所属 prefab 实例根 / stage 根 / 场景根」，确保节点外的引用也能重映射回新原生组件。
        /// </summary>
        static void RevertSelectedNodes()
        {
            var nodes = Selection.gameObjects;
            if (nodes.Length == 0) return;

            var report = new Report();
            var stage = PrefabStageUtility.GetCurrentPrefabStage();

            // 按 broadRoot 分组，每个 broadRoot 只快照一次
            var byRoot = new Dictionary<GameObject, List<GameObject>>();
            foreach (var n in nodes)
            {
                var br = GetBroadRoot(n);
                if (br == null) continue;
                if (!byRoot.TryGetValue(br, out var list))
                    byRoot[br] = list = new List<GameObject>();
                list.Add(n);
            }

            foreach (var kv in byRoot)
            {
                try
                {
                    RevertNodes(kv.Value, kv.Key, report);
                }
                catch (Exception e)
                {
                    report.Failed.Add($"{kv.Key.GetScenePath()}（{e.Message}）");
                    Debug.LogError($"[UIRevert] 还原节点异常：{kv.Key.GetScenePath()}\n{e}");
                }
            }

            // 在 Prefab Mode 内操作则把改动写回资产；场景内的改动直接落在场景（用户自行保存场景）
            if (stage != null)
                PrefabUtility.SaveAsPrefabAssetAndConnect(stage.prefabContentsRoot, stage.assetPath, InteractionMode.AutomatedAction);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            report.Log();
        }

        /// <summary>节点引用快照应覆盖到的最大范围：prefab 实例根（含 Prefab Mode 的 stage 根）或场景根。</summary>
        static GameObject GetBroadRoot(GameObject node)
        {
            if (node == null) return null;
            var instRoot = PrefabUtility.GetNearestPrefabInstanceRoot(node);
            if (instRoot != null) return instRoot;
            return node.transform.root != null ? node.transform.root.gameObject : node;
        }

        #endregion

        #region 报告（仅 Console）

        public class Report
        {
            public readonly List<string> Saved = new List<string>();
            public readonly List<string> Skipped = new List<string>();
            public readonly List<string> Failed = new List<string>();
            public readonly List<string> Warnings = new List<string>();

            public int Components;  // 还原的组件数
            public int Texts;       // 还原的多语言文本数
            public int Refs;        // 重映射的引用数
            public int UIStates;    // 修复的 UIState 数

            public void Warn(string msg)
            {
                Warnings.Add(msg);
            }

            public void Log()
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[UIRevert] 完成：目标 {Saved.Count + Skipped.Count + Failed.Count} | " +
                              $"已还原 {Saved.Count} | 无 Gen 特征跳过 {Skipped.Count} | 失败 {Failed.Count}");
                sb.AppendLine($"[UIRevert] 组件还原 {Components} 个 | 文本还原 {Texts} 条 | " +
                              $"引用重映射 {Refs} 处 | UIState 修复 {UIStates} 项");

                if (Warnings.Count > 0)
                {
                    sb.AppendLine($"[UIRevert] 告警 {Warnings.Count} 条：");
                    // 最多打 50 条，避免刷屏
                    for (int i = 0; i < Warnings.Count && i < 50; i++)
                        sb.AppendLine("    " + Warnings[i]);
                    if (Warnings.Count > 50)
                        sb.AppendLine($"    ... 其余 {Warnings.Count - 50} 条已省略");
                }

                Debug.Log(sb.ToString());
            }
        }

        #endregion
    }
}
