using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Umbreey;

namespace Umbreey.Editor
{
    public static class BundleMapBuilder 
    {
        public static List<string> BuildPreloadList(BundleMap bundleMap, List<string> rootABs)
        {
            var allABNames = new List<Stack<string>>();
            var builtABSet = new HashSet<string>();
            
            var bfsIdQueue = new Queue<string>();
            foreach (var rootId in rootABs)
            {
                var abNames = new Stack<string>();
                allABNames.Add(abNames);
                
                bfsIdQueue.Enqueue(rootId);
                while (bfsIdQueue.Count > 0)
                {
                    var bfsId = bfsIdQueue.Dequeue();
                    if (!builtABSet.Add(bfsId)) 
                        continue;
                    
                    var bfsRef = bundleMap.GetReference(bfsId);
                    if (bfsRef == null)
                    {
                        Debug.LogWarning($"can't find {bfsId} in bundlemap. so skip it");
                        continue;
                    }

                    abNames.Push(bfsRef.ABName);
                    foreach (var dependName in bfsRef.DependABNames)
                    {
                        int pos = dependName.LastIndexOf('_');
                        var dependId = dependName.Substring(0, pos);
                        bfsIdQueue.Enqueue(dependId);
                    }
                }      
            }
            
            // output
            var preloadList = new List<string>();
            foreach (var abNames in allABNames)
            {
                foreach (var abName in abNames)
                {
                    preloadList.Add(abName);
                }
            }
            
            return preloadList;
        }
        
        public static string Build(BundleMap bundleMap, int hotupdate = 0)
        {
            if (bundleMap == null)
                return null;
            
            bundleMap.Content.Meta.UpdateBuild = hotupdate;
            
            StringBuilder ret = new StringBuilder();
            foreach (var (_, line) in bundleMap.Lines)
            {
                ret.Append($"{line.text}\n");
            }
            ret.Append(BundleMap.kSeparator);
            ret.Append($"{JsonUtility.ToJson(bundleMap.Content, true)}\n");
            
            return ret.ToString();
        }

        public static BundleMap CollectUpdateData(string basePath, string newPath)
        {
            if (!File.Exists(basePath) || !File.Exists(newPath))
            {
                Debug.LogError($"BundleMapBuilder: Can't find base or new map. base: {basePath}, new: {newPath}");
                return null;
            }
            
            // load both
            var baseText = File.ReadAllText(basePath);
            var baseMap = new BundleMap(baseText);

            var newText = File.ReadAllText(newPath);
            var newMap = new BundleMap(newText);
            
            if (baseMap.Content.Meta.IsEncrypt != newMap.Content.Meta.IsEncrypt)
            {
                Debug.LogError($"BundleMapBuilder: encrypt flag is different between base and new map. {baseMap.Content.Meta.IsEncrypt} != {newMap.Content.Meta.IsEncrypt}");
            }

            if (baseMap.Content.Meta.Branch != newMap.Content.Meta.Branch)
            {
                Debug.LogError($"BundleMapBuilder: branch is different between base and new map. {baseMap.Content.Meta.Branch} != {newMap.Content.Meta.Branch}");
            }
            
            // compare lines
            Dictionary<string, BundleMap.LineData> updateLines = new Dictionary<string, BundleMap.LineData>();
            foreach (var (id, newLine) in newMap.Lines)
            {
                if (!baseMap.Lines.TryGetValue(id, out var baseLine) || !baseLine.Equals(newLine))
                {
                    updateLines.Add(id, newLine);
                }
            }
            
            var updateMeta = baseMap.Content.Meta.Clone();
            {
                var newMeta = newMap.Content.Meta;
                updateMeta.UpdateBuild = newMeta.Build;
                updateMeta.Version = newMeta.Version;
                updateMeta.Revision = newMeta.Revision;
            }
            
            var updateMap = new BundleMap(updateLines, newMap.Content)
            {
                Content = {
                    Meta = updateMeta // 对象初始化器语法，只更新content中的meta属性
                }
            };

            return updateMap;
        }
        
        public static BundleMap CollectData(AssetBundleBuilder.BuildData buildData, AssetBundleManifest manifest, string workingDir)
        {
            if (buildData == null || manifest == null)
                return null;
            
            Dictionary<string, BundleMap.LineData> lines = new Dictionary<string, BundleMap.LineData>();
            BundleMap.ContentData content = new BundleMap.ContentData();
            
            // bundles
            var bundleDict = buildData.BundleDict;
            foreach (var fileName in manifest.GetAllAssetBundles())
            {
                var bundleName = fileName.Substring(0, fileName.LastIndexOf('_'));
                if (bundleDict.TryGetValue(bundleName, out var bundleBuild))
                {
                    var depends = manifest.GetAllDependencies(fileName);
                    var md5 = manifest.GetAssetBundleHash(fileName).ToString();
                    
                    var line = BuildBundleLine(workingDir, fileName, md5, false);
                    if (line == null)
                        continue;
                    
                    lines.Add(line.id, line);
                    content.References.Add(new BundleMap.Reference()
                    {
                        ABName = fileName,
                        AssetNames = bundleBuild.addressableNames,
                        DependABNames = depends
                    });
                }
                else
                {
                    Debug.LogError($"BundleMapBuilder: Can't find build pack for bundle {fileName}");
                }
            }
            
            // raw files
            var rawDict= buildData.RawDict;
            foreach (var (_, raw) in rawDict)
            {
                var line = BuildBundleLine(workingDir, raw.fileName, raw.md5, true);
                if (line != null)
                {
                    lines.Add(line.id, line);
                }
            }
            
            return new BundleMap(lines, content);
        }

        private static BundleMap.LineData BuildBundleLine(string workingDir, string fileName, string md5, bool raw)
        {
            var file = $"{workingDir}/{fileName}";   
            if (!File.Exists(file))
            {
                Debug.LogError($"try build bundle line, but file is not exist. {file}");
                return null;
            }

            if (string.IsNullOrEmpty(md5))
            {
                Debug.LogError($"try build bundle line, but md5 is null. {file}");
                return null;
            }
            
            var id = Path.GetFileNameWithoutExtension(file).Replace($"_{md5}", "");
            var ext = Path.GetExtension(file).Replace(".", "");
            var size= new FileInfo(file).Length;

            string text = $"{id}:{md5}:{ext}:{size}:{(raw ? 1 : 0)}";
            return new BundleMap.LineData(text);
        }

    }    
}

