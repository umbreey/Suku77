using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Umbreey.Editor
{
    /// <summary>
    /// 引用重映射（UIGen.RestoreComps 的逆）与 UIState 修复。
    /// </summary>
    public static partial class UIRevert
    {
        /// <summary>
        /// 把指向自定义组件的引用改指回还原后的原生组件。
        /// 靠 SerializedProperty.type（形如 "PPtr&lt;$UIImage&gt;"）判断字段期望类型，
        /// 才能区分「Toggle.m_TargetGraphic(Graphic) 可回填」与「UIState.uiImage(UIImage) 塞不进原生组件」。
        /// </summary>
        static void RemapReferences(List<RefEntry> table, Dictionary<int, Component> map, Report report)
        {
            if (table == null || table.Count == 0 || map.Count == 0) return;

            // 按持有者分组，避免同一个 SerializedObject 反复创建/Apply
            var grouped = new Dictionary<Component, List<RefEntry>>(RefComparer.Instance);
            foreach (var e in table)
            {
                if (!(e.Target is Component)) continue;
                if (!map.ContainsKey(e.TargetId)) continue;
                if (e.Owner == null) continue;      // 宿主组件已被移除
                if (e.Owner is UIState) continue;   // UIState 走 FixUIState，这里跳过以免重复告警

                List<RefEntry> list;
                if (!grouped.TryGetValue(e.Owner, out list))
                {
                    list = new List<RefEntry>();
                    grouped[e.Owner] = list;
                }

                list.Add(e);
            }

            foreach (var kv in grouped)
            {
                var owner = kv.Key;
                SerializedObject so;
                try
                {
                    so = new SerializedObject(owner);
                }
                catch
                {
                    continue;
                }

                bool dirty = false;
                foreach (var e in kv.Value)
                {
                    var p = so.FindProperty(e.Path);
                    if (p == null) continue;

                    Component neo;
                    if (!map.TryGetValue(e.TargetId, out neo)) continue;

                    if (!CanAssign(e.ExpectType, neo))
                    {
                        // 字段类型本身就是自定义组件（如 UITravelSticker.imgStickerNew），
                        // 原生组件赋不进去，显式置空避免留下悬挂 fileID
                        report.Warn($"{owner.gameObject.GetScenePath()} | {owner.GetType().Name}.{e.Path} " +
                                    $"期望 {e.ExpectType ?? "?"}，无法接受 {neo.GetType().Name}");
                        p.objectReferenceValue = null;
                        dirty = true;
                        continue;
                    }

                    p.objectReferenceValue = neo;
                    report.Refs++;
                    dirty = true;
                }

                if (dirty) so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        static bool CanAssign(string expectTypeName, Object target)
        {
            if (string.IsNullOrEmpty(expectTypeName)) return true; // 解析不出来就放行，别误伤
            if (target == null) return false;

            var et = ResolveTypeByName(expectTypeName);
            return et == null || et.IsInstanceOfType(target);
        }

        static readonly Dictionary<string, Type> s_TypeCache = new Dictionary<string, Type>();

        static Type ResolveTypeByName(string name)
        {
            Type t;
            if (s_TypeCache.TryGetValue(name, out t)) return t;

            t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    t = asm.GetType(name) ?? asm.GetTypes().FirstOrDefault(x => x.Name == name);
                }
                catch
                {
                    continue; // ReflectionTypeLoadException，跳过该程序集
                }

                if (t != null) break;
            }

            s_TypeCache[name] = t;
            return t;
        }

        #region UIState

        /// <summary>
        /// Gen 里 UIState 的 image/textMesh 字段全是 0（运行时靠 go.GetComponent&lt;UIImage&gt;() 兜底），
        /// 组件换回原生后必须显式回填，否则 UIState 的图片/字体状态在运行时静默失效。
        /// </summary>
        static void FixUIState(GameObject root, Dictionary<int, Component> map, Report report)
        {
            foreach (var state in root.GetComponentsInChildren<UIState>(true))
            {
                if (state == null) continue;

                var so = new SerializedObject(state);
                var states = so.FindProperty("States");
                if (states == null) continue;

                bool dirty = false;
                for (int i = 0; i < states.arraySize; i++)
                {
                    var s = states.GetArrayElementAtIndex(i);
                    dirty |= FixStateArray<Image>(s, "stateImage", "uiImage", "image", map, report, state.gameObject);
                    dirty |= FixStateArray<TextMeshProUGUI>(s, "stateTextMesh", "uiTextMesh", "textMesh", map, report, state.gameObject);
                    dirty |= FixStateArray<Text>(s, "stateText", "uiText", "text", map, report, state.gameObject);
                }

                if (dirty)
                {
                    so.ApplyModifiedPropertiesWithoutUndo();
                    report.UIStates++;
                }
            }
        }

        static bool FixStateArray<T>(SerializedProperty state, string arrayProp, string customProp,
            string nativeProp, Dictionary<int, Component> map, Report report, GameObject ctx)
            where T : Component
        {
            var arr = state.FindPropertyRelative(arrayProp);
            if (arr == null || arr.arraySize == 0) return false;

            bool dirty = false;
            for (int i = 0; i < arr.arraySize; i++)
            {
                var e = arr.GetArrayElementAtIndex(i);

                // 优先从自定义组件字段查替换映射，其次原有原生引用，最后用 go 兜底（与运行时行为一致）
                T target = null;
                var custom = e.FindPropertyRelative(customProp)?.objectReferenceValue;
                if (custom is Component cc)
                {
                    Component neo;
                    if (map.TryGetValue(cc.GetInstanceID(), out neo)) target = neo as T;
                }

                if (target == null)
                    target = e.FindPropertyRelative(nativeProp)?.objectReferenceValue as T;

                if (target == null)
                {
                    var go = e.FindPropertyRelative("go")?.objectReferenceValue as GameObject;
                    if (go != null) target = go.GetComponent<T>();
                }

                var nativePropObj = e.FindPropertyRelative(nativeProp);
                if (nativePropObj != null)
                {
                    nativePropObj.objectReferenceValue = target;
                    dirty = true;
                }

                // 自定义组件字段类型不匹配原生组件，必须清掉
                var customPropObj = e.FindPropertyRelative(customProp);
                if (customPropObj != null)
                {
                    customPropObj.objectReferenceValue = null;
                    dirty = true;
                }

                if (target == null)
                {
                    report.Warn($"{ctx.GetScenePath()} UIState.{arrayProp}[{i}] 找不到原生 {typeof(T).Name}");
                    continue;
                }

                if (target is Image)
                    FixStateImageAsset(e, ctx, report);
                else if (target is TextMeshProUGUI)
                    FixStateTextMeshAsset(e, ctx, report);
                // StateText：UIState.Process() 没有清过它的 font/material，原引用仍有效，不要动
            }

            return dirty;
        }

        static void FixStateImageAsset(SerializedProperty e, GameObject ctx, Report report)
        {
            if (e.FindPropertyRelative("notSetSprite")?.boolValue ?? false) return;

            string spriteName = e.FindPropertyRelative("spriteName")?.stringValue;
            if (string.IsNullOrEmpty(spriteName)) return;

            var sprite = FindAsset<Sprite>(spriteName, ctx, report);
            if (sprite != null)
                e.FindPropertyRelative("sprite").objectReferenceValue = sprite;
        }

        static void FixStateTextMeshAsset(SerializedProperty e, GameObject ctx, Report report)
        {
            string fontName = e.FindPropertyRelative("fontName")?.stringValue;
            if (!string.IsNullOrEmpty(fontName))
            {
                var font = FindAsset<TMP_FontAsset>(fontName, ctx, report);
                if (font != null)
                    e.FindPropertyRelative("font").objectReferenceValue = font;
            }

            string materialName = e.FindPropertyRelative("materialName")?.stringValue;
            if (!string.IsNullOrEmpty(materialName))
            {
                var mat = FindTmpMaterial(materialName, ctx, report);
                if (mat != null)
                    e.FindPropertyRelative("material").objectReferenceValue = mat;
            }
        }

        #endregion
    }
}
