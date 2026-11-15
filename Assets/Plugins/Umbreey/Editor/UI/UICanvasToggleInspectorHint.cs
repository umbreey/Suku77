using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Umbreey;

namespace Umbreey.Editor
{
    /// <summary>
    /// Inspector 提示（作者态前哨）：当 inspected 组件满足以下条件时，在组件头部画黄色 HelpBox 提醒作者：
    ///   1) 组件含 OnDisable/OnEnable 且未补偿（未实现 IUIVisibleNotify、也不在确认安全类型白名单内）；
    ///   2) 所在物体处于 UI prefab 子树（向上找到名字以 "UI" 开头的祖先）；
    ///   3) 组件是自定义 Mono（非 Unity 原生）、非良性 B 类（UIImage/UIText 等）。
    ///
    /// 触发条件由「组件自身是否满足白名单准入条件」决定，不按「UI 是否进了 canvas-toggle 白名单」门禁——
    /// 因为白名单是 UIGen 对整棵 prefab 静态扫出的结果，作者本地预测不了；若按 UI 门禁，
    /// 会出现「现在没进白名单的 UI 加了新组件永远不被提醒，日后被扫进白名单即静默出错」的漏洞。
    ///
    /// 实现说明：Unity 禁止把 MonoBehaviour 根基类作为 [CustomEditor] 目标，故改为订阅
    /// Editor.finishedDefaultHeaderGUI 静态事件。注意：在本工程 Unity 版本（2022.3.62f3）中，该事件的
    /// editor.target 始终是「GameObject（根头部）」，并不会对每个组件头部各触发一次（组件的 target 不是 MonoBehaviour）。
    /// 因此这里从 GameObject 取出其全部 MonoBehaviour 组件，逐个判定，在根头部聚合画一条 HelpBox。
    /// </summary>
    [InitializeOnLoad]
    public static class UICanvasToggleInspectorHint
    {
        static UICanvasToggleInspectorHint()
        {
            // 每次程序集重加载都重新订阅，先 -= 再 += 防止重复挂载同一个 handler。
            UnityEditor.Editor.finishedDefaultHeaderGUI -= OnHeaderGUI;
            UnityEditor.Editor.finishedDefaultHeaderGUI += OnHeaderGUI;
        }

        private static void OnHeaderGUI(UnityEditor.Editor editor)
        {
            if (editor == null || editor.target == null) return;

            // 本版本中该事件的 target 是 GameObject（根头部），而非各组件；直接取它的全部 MonoBehaviour 组件统一判定。
            if (!(editor.target is GameObject go)) return;

            var offenders = new List<string>();
            foreach (var c in go.GetComponents<MonoBehaviour>())
            {
                if (c != null && ShouldWarn(c))
                    offenders.Add(c.GetType().Name);
            }

            if (offenders.Count == 0) return;

            var detail = offenders.Count == 1
                ? $"组件 {offenders[0]} 含 OnDisable/OnEnable"
                : $"以下组件含 OnDisable/OnEnable：{string.Join("、", offenders)}";

            EditorGUILayout.HelpBox(
                detail + "。若该 UI 被纳入 canvas-toggle 白名单（只关 Canvas、不触发子树 OnDisable），隐藏时这些逻辑不会执行；" +
                "请让组件实现 Umbreey.IUIVisibleNotify；若为第三方组件且确认安全，请加入 CanvasToggleSafety 的确认安全类型白名单。",
                MessageType.Warning);
        }

        private static bool ShouldWarn(MonoBehaviour mb)
        {
            var uiRoot = FindUIRoot(mb.gameObject);
            if (uiRoot == null) return false;

            // 不按 UI 是否进白名单做门禁。如需改为「仅白名单内且未知类型」才提示，取消下一行注释即可：
            // if (!UICanvasToggleValidator.IsWhitelisted(uiRoot.name)) return false;

            // 直接复用 Gen/Validator 共享的 CanvasToggleSafety 判定，避免两边逻辑漂移。
            // 组件“应被警告” ⇔ 它不满足 canvas 安全判定（即 Gen 会把它记为“未补偿”）。
            return !CanvasToggleSafety.IsComponentCanvasSafe(mb.GetType());
        }

        private static Transform FindUIRoot(GameObject go)
        {
            var t = go != null ? go.transform : null;
            while (t != null)
            {
                if (t.name.StartsWith("UI")) return t;
                t = t.parent;
            }
            return null;
        }

    }
}
