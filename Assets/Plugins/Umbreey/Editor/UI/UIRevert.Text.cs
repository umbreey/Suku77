using System;
using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Umbreey.Editor
{
    /// <summary>
    /// 文本类组件的还原 + 多语言还原。
    /// 字段清单与 UIGen.ReplaceTextMeshPro / ReplaceText 一一对应：正向拷了什么，反向就拷回什么。
    /// 销毁与读取顺序的原因见 UIRevert.Replace.cs 顶部注释。
    /// </summary>
    public static partial class UIRevert
    {
        static TextMeshProUGUI RestoreTextMeshPro(UITextMeshPro src, Report report)
        {
            var go = src.gameObject;
            var so = new SerializedObject(src);
            string fontName = so.FindProperty("m_FontName").stringValue;
            string materialName = so.FindProperty("m_MaterialName").stringValue;
            bool wasEnabled = src.enabled;

            Object.DestroyImmediate(src, true);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            if (tmp == null)
            {
                report.Warn($"{go.GetScenePath()} 添加 TextMeshProUGUI 失败");
                return null;
            }

            tmp.text = src.text;
            tmp.fontStyle = src.fontStyle;
            tmp.fontSize = src.fontSize;
            tmp.color = src.color;
            tmp.alignment = src.alignment;
            tmp.enableWordWrapping = src.enableWordWrapping;
            tmp.horizontalMapping = src.horizontalMapping;
            tmp.verticalMapping = src.verticalMapping;
            tmp.overflowMode = src.overflowMode;
            tmp.raycastTarget = src.raycastTarget;
            tmp.raycastPadding = src.raycastPadding;
            tmp.maskable = src.maskable;
            tmp.margin = src.margin;
            tmp.enableVertexGradient = src.enableVertexGradient;
            tmp.colorGradient = src.colorGradient;
            tmp.lineSpacing = src.lineSpacing;
            tmp.characterSpacing = src.characterSpacing;
            tmp.enableAutoSizing = src.enableAutoSizing;
            tmp.fontSizeMax = src.fontSizeMax;
            tmp.fontSizeMin = src.fontSizeMin;
            tmp.enableKerning = src.enableKerning;

            // 字体必须先赋：TMP 在设置 font 时会把材质重置成字体的默认材质
            if (!string.IsNullOrEmpty(fontName))
            {
                var font = FindAsset<TMP_FontAsset>(fontName, go, report);
                tmp.font = font != null ? font : src.font;
            }
            else
            {
                tmp.font = src.font;
            }

            // UIGen 只在「材质 != 字体自带材质」时才记名字，为空就让 TMP 用字体默认材质
            if (!string.IsNullOrEmpty(materialName))
            {
                var mat = FindTmpMaterial(materialName, go, report);
                if (mat != null) tmp.fontSharedMaterial = mat;
            }

            tmp.enabled = wasEnabled;
            return tmp;
        }

        static Text RestoreText(UIText src, Report report)
        {
            var go = src.gameObject;
            var so = new SerializedObject(src);
            string fontName = so.FindProperty("m_FontName").stringValue;
            string materialName = so.FindProperty("m_MaterialName").stringValue;
            bool wasEnabled = src.enabled;

            Object.DestroyImmediate(src, true);

            var text = go.AddComponent<Text>();
            if (text == null)
            {
                report.Warn($"{go.GetScenePath()} 添加 Text 失败");
                return null;
            }

            text.text = src.text;
            text.fontStyle = src.fontStyle;
            text.fontSize = src.fontSize;
            text.color = src.color;
            text.alignment = src.alignment;
            text.raycastTarget = src.raycastTarget;
            text.raycastPadding = src.raycastPadding;
            text.maskable = src.maskable;
            text.resizeTextForBestFit = src.resizeTextForBestFit;
            text.horizontalOverflow = src.horizontalOverflow;
            text.verticalOverflow = src.verticalOverflow;
            text.resizeTextMinSize = src.resizeTextMinSize;
            text.resizeTextMaxSize = src.resizeTextMaxSize;
            text.lineSpacing = src.lineSpacing;

            // UIGen 对工程内字体会把 uiText.font 置 null，只留名字，所以这里名字优先
            if (!string.IsNullOrEmpty(fontName))
            {
                var font = FindAsset<Font>(fontName, go, report);
                text.font = font != null ? font : src.font;
            }
            else
            {
                text.font = src.font;
            }

            if (!string.IsNullOrEmpty(materialName))
            {
                var mat = FindAsset<Material>(materialName, go, report);
                if (mat != null) text.material = mat;
            }

            text.enabled = wasEnabled;
            return text;
        }

        /// <summary>
        /// UIGen.ProcessUILang 把中文文本清空了，原文唯一的副本在 UILangLibrary.langs[].preview。
        /// 注意不能靠 UILang.library 反查 —— UIGen 只赋了 key，library 字段恒为空。
        /// 必须在销毁 UILangLibrary 之前调用。
        /// </summary>
        static void RestoreLangTexts(GameObject root, HashSet<GameObject> nodeFilter, Report report)
        {
            var dict = new Dictionary<string, string>();
            foreach (var lib in root.GetComponentsInChildren<UILangLibrary>(true))
            {
                if (lib == null || lib.langs == null) continue;
                foreach (var l in lib.langs)
                {
                    if (l == null || string.IsNullOrEmpty(l.key)) continue;
                    if (!dict.ContainsKey(l.key)) dict[l.key] = l.preview;
                }
            }

            foreach (var comp in root.GetComponentsInChildren<Component>(true))
            {
                if (!(comp is UITextMeshPro) && !(comp is UIText)) continue;
                // 节点模式：只还原落在所选节点子树内的文本组件；整 prefab 模式 nodeFilter 为 null（全量）
                if (nodeFilter != null && !IsUnderAny(comp.gameObject, nodeFilter)) continue;

                var so = new SerializedObject(comp);

                // key 优先取组件私有字段 m_lang.key，退化到节点上的 UILang.key
                string key = null;
                var lang = so.FindProperty("m_lang");
                if (lang != null)
                {
                    var keyProp = lang.FindPropertyRelative("key");
                    if (keyProp != null) key = keyProp.stringValue;
                }

                if (string.IsNullOrEmpty(key))
                {
                    var uiLang = comp.GetComponent<UILang>();
                    if (uiLang != null) key = uiLang.key;
                }

                if (string.IsNullOrEmpty(key)) continue;

                string value;
                if (!dict.TryGetValue(key, out value))
                {
                    report.Warn($"{comp.gameObject.GetScenePath()} 多语言 key={key} 在 UILangLibrary 中不存在，原文已丢失");
                    continue;
                }

                if (string.IsNullOrEmpty(value)) continue;

                var textProp = so.FindProperty("m_text");
                if (textProp == null) continue;

                textProp.stringValue = value;
                so.ApplyModifiedPropertiesWithoutUndo();
                report.Texts++;
            }
        }

        static void DestroyComponentsOfType(GameObject root, params Type[] types)
        {
            foreach (var type in types)
            {
                foreach (var c in root.GetComponentsInChildren(type, true))
                {
                    if (c != null)
                        Object.DestroyImmediate(c, true);
                }
            }
        }

        /// <summary>判断 go 是否落在 set 中任一节点的子树内（含节点自身）。</summary>
        static bool IsUnderAny(GameObject go, HashSet<GameObject> set)
        {
            var t = go.transform;
            foreach (var n in set)
                if (t.IsChildOf(n.transform)) return true;
            return false;
        }
    }
}
