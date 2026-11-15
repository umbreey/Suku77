using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Umbreey.Editor
{
    /// <summary>
    /// 资源按名反查 + 引用快照。
    /// 资源查找沿用 UITravelStickerGen 的做法：FindAssets + 完整文件名精确匹配，不建任何缓存（也不落盘）。
    /// </summary>
    public static partial class UIRevert
    {
        /// <summary>
        /// 用托管引用做 key 的比较器。
        /// 必须用它：map 的 key 是刚被 DestroyImmediate 掉的原组件，
        /// UnityEngine.Object 的 Equals/GetHashCode 在对象销毁后不可靠。
        /// </summary>
        sealed class RefComparer : IEqualityComparer<Component>
        {
            public static readonly RefComparer Instance = new RefComparer();

            public bool Equals(Component a, Component b)
            {
                return ReferenceEquals(a, b);
            }

            public int GetHashCode(Component c)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c);
            }
        }

        struct RefEntry
        {
            public Component Owner;
            public string Path;
            public Object Target;
            public string ExpectType;
            // 必须在销毁前从 SerializedProperty 取出：销毁后 objectReferenceValue 退化成 null，
            // GetInstanceID() 也不可靠，而它恰好等于 RevertRoot 里 map 的 key（旧组件 instanceID）。
            public int TargetId;
        }

        #region 按名找资源

        static string FindAssetPath(string fileName, GameObject ctx, Report report)
        {
            if (string.IsNullOrEmpty(fileName)) return null;

            // 与 UITravelStickerGen 一致：先按不带扩展名的名字搜，再用完整文件名精确匹配
            string stem = Path.GetFileNameWithoutExtension(fileName);
            foreach (var guid in AssetDatabase.FindAssets(stem))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(path) == fileName) return path;
            }

            // 兜底：名字含空格时（如 "DouyinSansBold SDF.asset"）FindAssets 会把搜索词拆开，
            // 直接在磁盘上按文件名找
            foreach (var path in Directory.GetFiles("Assets", fileName, SearchOption.AllDirectories))
                return path.Replace("\\", "/");

            report?.Warn($"{ctx.GetScenePath()} 找不到资源：{fileName}");
            return null;
        }

        static T FindAsset<T>(string fileName, GameObject ctx, Report report) where T : Object
        {
            string path = FindAssetPath(fileName, ctx, report);
            return path != null ? AssetDatabase.LoadAssetAtPath<T>(path) : null;
        }

        /// <summary>TMP 材质的名字可能是 ".mat"，也可能是 ".asset"（取字体资产的 material）。</summary>
        static Material FindTmpMaterial(string fileName, GameObject ctx, Report report)
        {
            string path = FindAssetPath(fileName, ctx, report);
            if (path == null) return null;

            if (path.EndsWith(".mat"))
                return AssetDatabase.LoadAssetAtPath<Material>(path);

            var tmp = AssetDatabase.LoadAssetAtPath<TMP_Asset>(path);
            return tmp != null ? tmp.material : null;
        }

        #endregion

        #region 引用快照

        /// <summary>必须在任何 DestroyImmediate 之前调用：销毁后 objectReferenceValue 会退化成 null。</summary>
        static List<RefEntry> SnapshotAllObjectRefs(GameObject root)
        {
            var list = new List<RefEntry>();
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c is Transform) continue; // 树结构引用与本次替换无关

                SerializedObject so;
                try
                {
                    so = new SerializedObject(c);
                }
                catch
                {
                    continue;
                }

                var it = so.GetIterator();
                while (it.Next(true))
                {
                    if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var target = it.objectReferenceValue;
                    if (target == null) continue;

                    list.Add(new RefEntry
                    {
                        Owner = c,
                        Path = it.propertyPath,
                        Target = target,
                        ExpectType = ParsePPtrType(it.type),
                        TargetId = it.objectReferenceInstanceIDValue
                    });
                }
            }

            return list;
        }

        /// <summary>"PPtr&lt;$UIImage&gt;" -> "UIImage"；"PPtr&lt;Material&gt;" -> "Material"</summary>
        static string ParsePPtrType(string type)
        {
            if (string.IsNullOrEmpty(type)) return null;
            int a = type.IndexOf('<');
            int b = type.LastIndexOf('>');
            if (a < 0 || b <= a) return null;
            return type.Substring(a + 1, b - a - 1).TrimStart('$');
        }

        #endregion
    }
}
