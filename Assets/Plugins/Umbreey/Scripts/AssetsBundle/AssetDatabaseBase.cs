using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Umbreey
{
    public abstract class AssetDatabaseBase
    {
        protected BundleMap s_BundleMap = new BundleMap();
        
        // database
        protected bool s_BundleEncrypt => s_BundleMap.Meta.IsEncrypt;
        protected Dictionary<string, (string ABName, string[] DependABNames)> s_RuntimeDatabase = new Dictionary<string, (string, string[])>(); // assetName -> (abName, dependABNames)
        
        // cache
        protected Dictionary<string, AssetBundle> s_CacheBundleObject = new Dictionary<string, AssetBundle>(); 
        
        public abstract string GetFilePath(string aliasName);
        public abstract IEnumerator LoadRuntimeDatabase(Action<bool> callback);
        protected abstract IEnumerator LoadRuntimeBundleAsync(string abName);
        
        public BundleMap GetBundleMap()
        {
            return s_BundleMap;
        }
        
        protected virtual void SetRuntimeDatabase(BundleMap bundleMap)
        {
            bundleMap ??= new BundleMap();
            s_BundleMap = bundleMap;
            
            s_RuntimeDatabase = new Dictionary<string, (string, string[])>();
            foreach (var reference in bundleMap.Content.References)
            {
                foreach (var assetName in reference.AssetNames)
                {
                    s_RuntimeDatabase[assetName] = (reference.ABName, reference.DependABNames);
                }
            }
        }
        
        public virtual bool HasAsset(string assetName)
        {
            return !string.IsNullOrEmpty(assetName) && s_RuntimeDatabase.ContainsKey(assetName);
        }
        
        public virtual int GetCachedBundleCount()
        {
            return s_CacheBundleObject.Count;
        }
        
        public virtual string GetBundleNames(string assetName, out string[] dependABNames)
        {
            if (s_RuntimeDatabase.TryGetValue(assetName, out var data))
            {
                dependABNames = data.DependABNames;
                return data.ABName;
            }
            
            dependABNames = Array.Empty<string>();
            Log.E($"[Assets] assetName:{assetName} not found in runtime database");
            
            return null;
        }
        
        public virtual bool TryGetBundleForAsset(string assetName, out AssetBundle bundleObject)
        {
            bundleObject = null;
            
            var abName = GetBundleNames(assetName, out _);
            if (string.IsNullOrEmpty(abName))
                return false;
            
            return s_CacheBundleObject.TryGetValue(abName, out bundleObject);
        }

        public virtual void UnloadUnusedBundles(HashSet<string> usingNames)
        {
            for (int i = s_CacheBundleObject.Count - 1; i >= 0; i--)
            {
                var abName = s_CacheBundleObject.ElementAt(i).Key;
                if (!usingNames.Contains(abName))
                {
                    s_CacheBundleObject[abName].Unload(false);
                    s_CacheBundleObject.Remove(abName);
                }
            }
        }
        
        public IEnumerator LoadRuntimeBundleDependenciesAsync(string assetName)
        {
            var abName = GetBundleNames(assetName, out var dependNames);
            if (string.IsNullOrEmpty(abName))
            {
                yield break;
            }

            foreach (var depend in dependNames)
            {
                for (int i = 0; i < Assets.KRetryTimes; i++)
                {
                    yield return Umbreey.Inst.StartCoroutine(LoadRuntimeBundleAsync(depend));
                    
                    if (!s_CacheBundleObject.ContainsKey(depend))
                        Log.W($"[Assets] depend ab:{depend} retry:{i+1}/{Assets.KRetryTimes} ..");
                    else
                        break;
                }
                
                if (!s_CacheBundleObject.ContainsKey(depend))
                {
                    Log.E($"[Assets] depend ab failure after tried {Assets.KRetryTimes} times. name:{depend}");
                }
            }
            
            for (int i = 0; i < Assets.KRetryTimes; i++)
            {
                yield return Umbreey.Inst.StartCoroutine(LoadRuntimeBundleAsync(abName));
                
                if (!s_CacheBundleObject.ContainsKey(abName))
                    Log.W($"[Assets] ab:{abName}. retry:{i+1}/{Assets.KRetryTimes} ..");
                else
                    break;
            }
            
            if (!s_CacheBundleObject.ContainsKey(abName))
            {
                Log.E($"[Assets] ab failure after tried {Assets.KRetryTimes} times. name:{abName}");
            }
        }
    }
}
