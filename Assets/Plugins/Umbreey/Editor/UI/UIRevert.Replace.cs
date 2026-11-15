using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Umbreey.Editor
{
    /// <summary>
    /// UIGen.Replace.ReplaceRoot 的逆操作主体：把自定义组件换回原生组件。
    ///
    /// 写法对齐 UITravelStickerGen.RestoreImage / RestoreTextMeshProp：
    ///   读名字字段 -> 销毁自定义组件 -> AddComponent 原生组件 -> 逐字段回填 -> 按名字反查资源。
    ///
    /// 【为什么必须先销毁再 AddComponent】
    /// UnityEngine.UI.Graphic 上有 [DisallowMultipleComponent]（Graphic.cs:17）。
    /// UIImage/UIRawImage/UIText/UITextMeshPro 全部派生自 Graphic，
    /// 所以自定义组件还挂在 GameObject 上时 AddComponent&lt;Image&gt;() 会被 Unity 拒绝。
    ///
    /// 【为什么销毁之后还能读旧组件的字段】
    /// UGUI/TMP 组件都是 MonoBehaviour，m_Color / m_Sprite / m_text 这些序列化数据存在
    /// 托管 C# 对象上，DestroyImmediate 只销毁 native 侧，托管字段依然可读。
    /// 但 .gameObject / .enabled 这类会走 native 的成员销毁后不可用，必须提前取出。
    /// UIGen 与 UITravelStickerGen 都依赖这一行为。
    ///
    /// 【整体顺序是硬约束】
    ///   引用快照（销毁后 objectReferenceValue 就读不回来了）
    ///   -> 文本还原（原文只存在于 UILangLibrary.langs[].preview，销毁 library 就没了）
    ///   -> 删 UILang / UILangLibrary / UIBind / UIBindItem
    ///   -> 组件替换（把 old->new 映射建全）
    ///   -> 引用回填 / UIState 修复
    /// </summary>
    public static partial class UIRevert
    {
        /// <summary>整棵子树还原（资产流程 / Prefab Mode 整 prefab 流程共用）。</summary>
        static void RevertRoot(GameObject root, Report report)
        {
            var refTable = SnapshotAllObjectRefs(root);

            RestoreLangTexts(root, null, report);
            DestroyComponentsOfType(root, typeof(UILang), typeof(UILangLibrary));
            DestroyComponentsOfType(root, typeof(UIBind), typeof(UIBindItem));

            var map = BuildReplaceMap(root, report);

            RemapReferences(refTable, map, report);
            FixUIState(root, map, report);
        }

        /// <summary>
        /// 还原若干 UI 节点（各自的子树），在 broadRoot 范围内做引用快照与 UIState 修复，
        /// 从而把节点外指向被还原组件的引用也重映射回新原生组件。
        /// 与整 prefab 还原的区别：多语言库 / UIBind 只清理「节点内」的，避免误删节点外的共享组件。
        /// </summary>
        static void RevertNodes(IEnumerable<GameObject> nodes, GameObject broadRoot, Report report)
        {
            var nodeSet = new HashSet<GameObject>(nodes);
            var refTable = SnapshotAllObjectRefs(broadRoot);

            RestoreLangTexts(broadRoot, nodeSet, report);

            DestroyComponentsInNodes(nodes, typeof(UILang), typeof(UILangLibrary));
            DestroyComponentsInNodes(nodes, typeof(UIBind), typeof(UIBindItem));

            var map = BuildReplaceMapInNodes(nodes, report);

            RemapReferences(refTable, map, report);
            FixUIState(broadRoot, map, report);
        }

        static Dictionary<int, Component> BuildReplaceMap(GameObject root, Report report)
        {
            var map = new Dictionary<int, Component>();
            foreach (var old in CollectReplaceTargets(root))
            {
                if (old == null) continue;
                int id = old.GetInstanceID(); // 必须在销毁前取
                var neo = RestoreOne(old, report);
                if (neo != null)
                {
                    map[id] = neo;
                    report.Components++;
                }
            }
            return map;
        }

        static Dictionary<int, Component> BuildReplaceMapInNodes(IEnumerable<GameObject> nodes, Report report)
        {
            var map = new Dictionary<int, Component>();
            foreach (var old in CollectReplaceTargetsInNodes(nodes))
            {
                if (old == null) continue;
                int id = old.GetInstanceID(); // 必须在销毁前取
                var neo = RestoreOne(old, report);
                if (neo != null)
                {
                    map[id] = neo;
                    report.Components++;
                }
            }
            return map;
        }

        static List<Component> CollectReplaceTargetsInNodes(IEnumerable<GameObject> nodes)
        {
            var list = new List<Component>();
            var seen = new HashSet<int>();
            foreach (var node in nodes)
            {
                if (node == null) continue;
                foreach (var c in node.GetComponentsInChildren<Component>(true))
                {
                    if (c == null) continue;
                    if (c is UIImage || c is UIRawImage || c is UITextMeshPro || c is UIText)
                    {
                        int id = c.GetInstanceID();
                        if (seen.Add(id)) list.Add(c);
                    }
                }
            }
            return list;
        }

        static void DestroyComponentsInNodes(IEnumerable<GameObject> nodes, params Type[] types)
        {
            var seen = new HashSet<int>();
            foreach (var type in types)
            {
                foreach (var node in nodes)
                {
                    if (node == null) continue;
                    foreach (var c in node.GetComponentsInChildren(type, true))
                    {
                        if (c == null) continue;
                        int id = c.GetInstanceID();
                        if (!seen.Add(id)) continue;
                        Object.DestroyImmediate(c, true);
                    }
                }
            }
        }

        static List<Component> CollectReplaceTargets(GameObject root)
        {
            var list = new List<Component>();
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                if (c is UIImage || c is UIRawImage || c is UITextMeshPro || c is UIText)
                    list.Add(c);
            }

            return list;
        }

        static Component RestoreOne(Component old, Report report)
        {
            if (old is UIImage) return RestoreImage((UIImage)old, report);
            if (old is UIRawImage) return RestoreRawImage((UIRawImage)old, report);
            if (old is UITextMeshPro) return RestoreTextMeshPro((UITextMeshPro)old, report);
            return RestoreText((UIText)old, report);
        }

        static Image RestoreImage(UIImage src, Report report)
        {
            // ---- 销毁前必须取出的东西（native 成员 + SerializedObject）----
            var go = src.gameObject;
            var so = new SerializedObject(src);
            string spriteName = so.FindProperty("m_SpriteName").stringValue;
            string materialName = so.FindProperty("m_MaterialName").stringValue;
            bool wasEnabled = src.enabled;
            bool hasCustomMaterial = src.material != Graphic.defaultGraphicMaterial;

            Object.DestroyImmediate(src, true);

            var image = go.AddComponent<Image>();
            if (image == null)
            {
                report.Warn($"{go.GetScenePath()} 添加 Image 失败");
                return null;
            }

            image.color = src.color;
            image.raycastTarget = src.raycastTarget;
            image.raycastPadding = src.raycastPadding;
            image.maskable = src.maskable;
            image.type = src.type;
            image.fillCenter = src.fillCenter;

            if (src.type == Image.Type.Simple)
            {
                image.useSpriteMesh = src.useSpriteMesh;
                image.preserveAspect = src.preserveAspect;
            }
            else if (src.type == Image.Type.Sliced || src.type == Image.Type.Tiled)
            {
                image.pixelsPerUnitMultiplier = src.pixelsPerUnitMultiplier;
            }
            else if (src.type == Image.Type.Filled)
            {
                image.fillMethod = src.fillMethod;
                image.fillOrigin = src.fillOrigin;
                image.fillAmount = src.fillAmount;
                image.fillClockwise = src.fillClockwise;
                image.preserveAspect = src.preserveAspect;
            }

            // 精灵：UIGen 把它换成了 EmptySprite，只能按名字找回；找不到就沿用原引用
            if (!string.IsNullOrEmpty(spriteName))
            {
                var sprite = FindAsset<Sprite>(spriteName, go, report);
                image.sprite = sprite != null ? sprite : src.sprite;
            }
            else
            {
                image.sprite = src.sprite; // builtin 分支：UIGen 没写名字，原引用就是真的
            }

            // 材质：名字为空代表用默认材质
            if (!string.IsNullOrEmpty(materialName))
            {
                var mat = FindAsset<Material>(materialName, go, report);
                if (mat != null) image.material = mat;
                else if (hasCustomMaterial) image.material = src.material;
            }
            else if (hasCustomMaterial)
            {
                image.material = src.material;
            }

            image.enabled = wasEnabled;
            return image;
        }

        static RawImage RestoreRawImage(UIRawImage src, Report report)
        {
            var go = src.gameObject;
            var so = new SerializedObject(src);
            string texName = so.FindProperty("m_SpriteName").stringValue; // 这里存的是 texture 的文件名
            string materialName = so.FindProperty("m_MaterialName").stringValue;
            bool wasEnabled = src.enabled;
            bool hasCustomMaterial = src.material != Graphic.defaultGraphicMaterial;

            Object.DestroyImmediate(src, true);

            var raw = go.AddComponent<RawImage>();
            if (raw == null)
            {
                report.Warn($"{go.GetScenePath()} 添加 RawImage 失败");
                return null;
            }

            raw.color = src.color;
            raw.raycastTarget = src.raycastTarget;
            raw.raycastPadding = src.raycastPadding;
            raw.maskable = src.maskable;
            raw.uvRect = src.uvRect;

            if (!string.IsNullOrEmpty(texName))
            {
                var tex = FindAsset<Texture2D>(texName, go, report);
                raw.texture = tex != null ? tex : src.texture;
            }
            else
            {
                raw.texture = src.texture;
            }

            if (!string.IsNullOrEmpty(materialName))
            {
                var mat = FindAsset<Material>(materialName, go, report);
                if (mat != null) raw.material = mat;
                else if (hasCustomMaterial) raw.material = src.material;
            }
            else if (hasCustomMaterial)
            {
                raw.material = src.material;
            }

            raw.enabled = wasEnabled;
            return raw;
        }
    }
}
