using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Umbreey;

namespace Umbreey.Editor
{
    /// <summary>
    /// canvas-toggle 白名单安全校验器。
    ///
    /// 问题：canvas-toggle 隐藏只关 Canvas，不触发子树 OnDisable。若某自定义 MonoBehaviour 含 OnDisable 逻辑
    /// （清理/反注册/停协程等）却未实现 IUIVisibleNotify、也不在「确认安全类型白名单」或良性 B 类内，
    /// 它所在 UI 被隐藏时逻辑会静默不跑——无报错、无提示。
    ///
    /// 本校验器在 UIGen 生成白名单后、以及每次程序集重编译后，对白名单中每个 UI prefab 用 CanvasToggleSafety
    /// 共享判定扫描其自定义 Mono 是否“canvas 安全”；不安全则给出提示（默认警告，kTreatAsError 可升级为报错）。
    /// 安全判定与白名单生成（UICanvasToggleGen）共用同一套逻辑，避免两边漂移。
    ///
    /// 只查自定义 Mono，跳过 Unity 原生组件（UnityEngine.* / UnityEditor.* 命名空间或程序集）。
    /// </summary>
    public static class UICanvasToggleValidator
    {
        // true 时把提示升级为报错；默认 false（仅提示，不阻断）。
        public const bool kTreatAsError = false;

        private static HashSet<string> s_LastWhitelist;

        // ---- 后编译自动复检 ----
        static UICanvasToggleValidator()
        {
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
        }

        private static void OnAfterAssemblyReload()
        {
            if (EditorApplication.isPlaying) return;
            var config = UICanvasToggleGen.GetConfig();
            var wl = UICanvasToggleGen.ComputeWhitelist(config);
            Validate(config, wl);
        }

        // ---- 供 Inspector 提示读取的缓存 ----
        public static bool IsWhitelisted(string uiName)
        {
            if (s_LastWhitelist == null) EnsureWhitelistCached();
            return s_LastWhitelist != null && s_LastWhitelist.Contains(uiName);
        }

        public static void EnsureWhitelistCached()
        {
            if (s_LastWhitelist != null) return;
            try
            {
                var config = UICanvasToggleGen.GetConfig();
                s_LastWhitelist = new HashSet<string>(UICanvasToggleGen.ComputeWhitelist(config));
            }
            catch
            {
                s_LastWhitelist = new HashSet<string>();
            }
        }

        // ---- 主校验 ----
        public static void Validate(UIGen.Config config, List<string> whitelist)
        {
            if (whitelist == null || whitelist.Count == 0) return;
            string dir = config.DstPrefabPath;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            var nameToPath = new Dictionary<string, string>();
            foreach (var p in GetAllPrefabsInDirectory(dir))
                nameToPath[Path.GetFileNameWithoutExtension(p)] = p;

            int hit = 0;
            foreach (var name in whitelist)
            {
                if (!nameToPath.TryGetValue(name, out var path)) continue;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                hit += ValidatePrefab(prefab, name);
            }

            if (hit > 0)
            {
                if (kTreatAsError)
                    Debug.LogError($"[UICanvasToggleValidator] 发现 {hit} 处组件需补偿（canvas-toggle 隐藏不会触发其 OnDisable）。");
                else
                    Debug.LogWarning($"[UICanvasToggleValidator] 发现 {hit} 处组件需补偿（仅提示，不阻断）。请实现 Umbreey.IUIVisibleNotify；若为第三方组件且确认安全，加入 CanvasToggleSafety 的确认安全类型白名单。");
            }
            // 更新缓存，供 Inspector 读取
            s_LastWhitelist = new HashSet<string>(whitelist);
        }

        private static int ValidatePrefab(GameObject prefab, string uiName)
        {
            int hit = 0;
            var comps = prefab.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (var c in comps)
            {
                if (c == null) continue;
                var type = c.GetType();
                if (CanvasToggleSafety.IsComponentCanvasSafe(type)) continue; // 安全：无需补偿
                Report(prefab, uiName, type.Name);
                hit++;
            }
            return hit;
        }

        private static void Report(UnityEngine.Object ctx, string uiName, string compName)
        {
            string msg = $"[CanvasToggle] UI '{uiName}' 走 canvas-toggle 隐藏时，组件 '{compName}' 的 OnDisable/OnEnable 不会触发；" +
                         $"若其隐藏逻辑需要做清理/反注册/停协程，请实现 Umbreey.IUIVisibleNotify；若为第三方组件且确认安全，请加入 CanvasToggleSafety 的确认安全类型白名单，否则可能静默出错。";
            if (kTreatAsError) Debug.LogError(msg, ctx);
            else Debug.LogWarning(msg, ctx);
        }

        // ---- 目录遍历（与 UICanvasToggleGen 一致，扫 Gen 副本）----
        private static List<string> GetAllPrefabsInDirectory(string directory)
        {
            var paths = new List<string>();
            if (!Directory.Exists(directory)) return paths;
            DepthFirstSearch(directory, paths);
            return paths;
        }
        private static void DepthFirstSearch(string dir, List<string> outPaths)
        {
            outPaths.AddRange(Directory.GetFiles(dir, "*.prefab", SearchOption.TopDirectoryOnly));
            foreach (var sub in Directory.GetDirectories(dir, "*", SearchOption.TopDirectoryOnly))
                DepthFirstSearch(sub, outPaths);
        }
    }
}
