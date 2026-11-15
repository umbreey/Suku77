using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Umbreey.Editor
{
    public static class AssetBundleBuilder
    {
        public class AssetRawBuild
        {
            public string assetName;
            public string md5;
            public string fileName; // 输出文件名，{id}_{md5}.{ext}
        }
        
        public class BuildData
        {
            public readonly Dictionary<string, AssetBundleBuild> BundleDict = new Dictionary<string, AssetBundleBuild>(); // bundleName -> bundleBuild
            public readonly Dictionary<string, AssetRawBuild> RawDict = new Dictionary<string, AssetRawBuild>(); // aliasName -> rawBuild
        }
        
        public static readonly string DefaultOutput = $"{Application.dataPath}/../assetbundle";

        
        public static void CollectDataFromSinglePack(ABPack pack, ref BuildData inoutResult)
        {
            var allList = pack.Pack();
            
            // check
            var validList = new List<ABRuleBase.BuildData>();
            foreach (var data in allList)
            {
                if (data.assetNames == null || data.aliasNames == null || data.assetNames.Length == 0)
                {
                    Debug.LogWarning($"Skip bundle {data.bundleName} because it has no assets");
                    continue;
                }
     
                if (data.assetNames.Length != data.aliasNames.Length)
                {
                    Debug.LogError($"Skip bundle {data.bundleName} because asset count doesn't match alias count. {data.assetNames.Length} != {data.aliasNames.Length}");
                    continue;
                }
                validList.Add(data);
            }
            
            // collect
            if (pack.Rule is ABRuleRawFiles) // 如果是原始文件
            {
                foreach (var data in validList)
                {
                    int count = data.assetNames.Length;
                    for (int i = 0; i < count; i++)
                    {
                        var aliasName = data.aliasNames[i];
                        var assetName = data.assetNames[i];
                        if (inoutResult.RawDict.TryGetValue(aliasName, out var build))
                        {
                            Debug.LogError($"Raw asset {aliasName} already exists, this may cause data loss. existing: {build.assetName} -> new: {assetName}");
                        }
                        
                        var fileName = ABRuleRawFiles.GetFileMd5Name(assetName, out var md5);
                        inoutResult.RawDict[aliasName] = new AssetRawBuild()
                        {
                            assetName = assetName,
                            md5 = md5,
                            fileName = fileName,
                        };
                    }
                }
                
            }
            else // 默认是打包成AssetBundle
            {
                 foreach (var data in validList)
                 {
                     var abb = new AssetBundleBuild()
                     {
                         assetBundleName = data.bundleName, 
                         assetBundleVariant = data.bundleVariant,
                         assetNames = data.assetNames,
                         addressableNames = data.aliasNames
                     };

                     if (inoutResult.BundleDict.ContainsKey(data.bundleName))
                     {
                         Debug.LogError($"Bundle {data.bundleName} already exists, this may cause data loss");
                     }
                     
                     inoutResult.BundleDict[data.bundleName] = abb; 
                     //Debug.Log(data.assetBundleName + " " + string.Join(',', data.assetNames) + " " + string.Join(',', data.aliasNames));
                 }
            }
            
        }
        
        public static List<ABPack> FindAllPacks()
        {
            List<ABPack> packs = new List<ABPack>();
            foreach (var guid in AssetDatabase.FindAssets("t:ABPack"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var pack = AssetDatabase.LoadAssetAtPath<ABPack>(path);
                packs.Add(pack);
            }

            return packs;
        }
        
        public static BuildData CollectData(bool onlyBuiltin = false)
        {
            BuildData buildData = new BuildData();

            var packs = FindAllPacks();
            foreach (var pack in packs)
            {
                if (pack.Builtin || !onlyBuiltin)
                {
                    CollectDataFromSinglePack(pack, ref buildData);                    
                }
                else
                {
                    Debug.Log($"Skip pack {pack.name} because it's not builtin");
                }
            }

            return buildData;
        }
        
        public static AssetBundleManifest Build(BuildData buildData, string outputDir = null)
        {
            outputDir ??= DefaultOutput;
            PathUtils.RecreateDirectory(outputDir);
            
            // BuildAssetBundleOptions.DisableWriteTypeTree will cause editor can't load remote built assetbundles
            List<AssetBundleBuild> builds = buildData.BundleDict.Select(item => item.Value).ToList();
            var manifest = BuildPipeline.BuildAssetBundles(
                outputDir, 
                builds.ToArray(),
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.AppendHashToAssetBundleName,
                EditorUserBuildSettings.activeBuildTarget);
            
            string result = manifest != null ? "success" : "failed";
            Debug.Log($"Build assetbundle {result}");
            
            return manifest;
        }
    }
}