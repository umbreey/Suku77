using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using Object = UnityEngine.Object;

namespace Umbreey
{
    [CreateAssetMenu(menuName ="Umbreey/打包/AB配置文件")]
    public class ABPack : ScriptableObject
    {
        public string PackName;
        public string PackVariant = ABRuleBase.Variants.FirstOrDefault();
        public string Extension = "*.prefab,*.anim,*.fbx,*.mat,*.png";
        public ABRuleBase Rule;

        public bool Builtin = true;
        public List<Object> DirAssets;
        public List<Object> SingleAssets;
        
#if UNITY_EDITOR
        private HashSet<string> SplitExtensions()
        {
            var exts = Extension.Split(",").Select(ext => ext.Trim().ToLower()).ToList();
            return new HashSet<string>(exts);
        }

        public void CollectFilesTo(string[] allfiles, List<string> inoutlist)
        {
            if (allfiles == null || allfiles.Length == 0)
                return;
            
            HashSet<string> exts = SplitExtensions();
            foreach (var file in allfiles)
            {
                if (string.IsNullOrEmpty(file) || file.Contains(".svn"))
                    continue;
            
                var ext = Path.GetExtension(file).ToLower();
                if (ext == ".meta")
                    continue;
            
                if (exts.Contains("*.*") || exts.Contains($"*{ext}"))
                {
                    var path = file.Replace("\\", "/");
                    inoutlist.Add(path);
                }
            }
        }

        public void CollectObjectsTo(List<Object> allObjects, List<string> inoutlist)
        {
            List<string> files = new List<string>();
            foreach (var obj in allObjects)
            {
                if (obj == null)
                    continue;
                
                var file = AssetDatabase.GetAssetPath(obj);
                files.Add(file);
            }
            
            CollectFilesTo(files.ToArray(), inoutlist);                
        }
        
        public List<string> GetFiles()
        {
            List<string> list = new List<string>();
            
            foreach (var dirAsset in DirAssets)
            {
                if (dirAsset == null) 
                    continue;
                
                var dir = AssetDatabase.GetAssetPath(dirAsset);
                if (!AssetDatabase.IsValidFolder(dir)) 
                    continue;
                
                var files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories);
                CollectFilesTo(files, list);
            }

            CollectObjectsTo(SingleAssets, list);
            return list;
        }
        
        public List<ABRuleBase.BuildData> Pack()
        {
            return Rule.Pack(this);
        }
#endif
    }


}

