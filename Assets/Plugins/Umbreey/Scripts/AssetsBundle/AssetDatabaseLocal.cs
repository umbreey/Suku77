using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Umbreey
{
    public class AssetDatabaseLocal : AssetDatabaseBase
    {
        private Dictionary<string, AssetBundleCreateRequest> s_PendingBundleLocalRequest = new Dictionary<string, AssetBundleCreateRequest>();
        private static HashSet<string> s_HotupdateABName = new HashSet<string>();
        
        public override string GetFilePath(string aliasName) 
        {
            string id = Path.GetFileNameWithoutExtension(aliasName); 
            var line = s_BundleMap.GetLineData(id);
            if (line == null)
            {
                Log.W($"[Assets] local database can't find file. {aliasName}");
                return null;
            }

            var fileName = line.name;
            if (s_HotupdateABName.Contains(fileName))
            {
                return $"{Download.UMBREEY_DOWNLOAD_PATH}/{fileName}";
            }

            return $"{FileUtils.GetStreamingAssetsPath()}/{fileName}";
        }
        
        public void HotUpdateRuntimeDatabase(BundleMap bundleMap)
        {
            if (bundleMap == null)
                return;
            
            Log.I($"[Assets] local database hot update. build:{bundleMap.Meta.Build} update:{bundleMap.Meta.UpdateBuild} count:{bundleMap.Lines.Count}");
            SetRuntimeDatabase(bundleMap);
            
            foreach (var (_, line) in bundleMap.Lines)
            {
                s_HotupdateABName.Add(line.name);
            }
        }
        
        public override IEnumerator LoadRuntimeDatabase(Action<bool> callback)
        {
            s_RuntimeDatabase?.Clear();
            
            var text = FileUtils.LoadStreamingText(Assets.KBundleMapFileName);
            if (!string.IsNullOrEmpty(text))
            {
                BundleMap bundleMap = new BundleMap(text);
                SetRuntimeDatabase(bundleMap);
                
                callback?.Invoke(true);
                yield break;
            }

            Log.E($"[Assets] local bundle map failure. path:StreamingAssets/{Assets.KBundleMapFileName}");
            callback?.Invoke(false);
        }
        
        protected override IEnumerator LoadRuntimeBundleAsync(string abName)
        {
            if (s_CacheBundleObject.ContainsKey(abName))
            {
                yield break;
            }
            
            if (s_PendingBundleLocalRequest.TryGetValue(abName, out var localRequest))
            {
                while (!localRequest.isDone)
                    yield return null; // wait for exist local request finish
            }
            else
            {
                string headerUrl = s_HotupdateABName.Contains(abName) ? Download.UMBREEY_DOWNLOAD_PATH : Application.streamingAssetsPath;
                string loadPath = $"{headerUrl}/{abName}";
                localRequest = AssetBundle.LoadFromFileAsync(loadPath,0, s_BundleEncrypt ? 1u : 0u);
                
                s_PendingBundleLocalRequest[abName] = localRequest;
                yield return localRequest;
                s_PendingBundleLocalRequest.Remove(abName);
            }  
            
            var bundleObject = localRequest.assetBundle;
            if (bundleObject != null)
            {
                s_CacheBundleObject.TryAdd(abName, bundleObject);
            }
        }
    }
}
