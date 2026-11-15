using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
    using UnityEditor;
#endif
    
namespace Umbreey
{
    public class Assets
    {
        private class AssetCreateRequest
        {
            public AssetBundleRequest Request;
            public bool Irreversible;
        }
        
        private class AssetObject
        {
            public string AssetName;
            public string ABName;
            public string[] DependABNames;
            
            public int RefCount;
            public WeakReference WeakRef;

            public bool InUsing => (IsAlive && RefCount > 0);
            public bool IsAlive
            {
                get
                {
                    if (AssetName.EndsWith(".unity"))
                    {
                        var sceneName = Path.GetFileNameWithoutExtension(AssetName);
                        var scene = SceneManager.GetSceneByName(sceneName);
                        return scene.IsValid();
                    }

                    return IsAliveType<UnityEngine.Object>();
                }
            }

            public bool IsAliveType<T>() where T:UnityEngine.Object
            {
                return WeakRef is { IsAlive: true, Target: T };   
            }
        }

        private class InstObject
        {
            public AssetObject AssetObject;
            public WeakReference WeakRef;
            
            public bool IsUsing => WeakRef is { IsAlive: true };
        }
        
        // cache
        private static Dictionary<string, AssetCreateRequest> s_PendingAssetRequest = new Dictionary<string, AssetCreateRequest>();
        private static Dictionary<string, AssetObject> s_CacheAssetObject = new Dictionary<string, AssetObject>();
        private static List<InstObject> s_CacheInstObject = new List<InstObject>();
        
        // provider
        private static AssetDatabaseBase s_AssetDatabase;
        public const int KRetryTimes = 3;

        public static BundleMap.MetaData Meta => GetBundleMap()?.Meta ?? new BundleMap.MetaData();
        public const string KBundleMapFileName = "bundlemap.txt";
		
        public static BundleMap GetBundleMap()
        {
            return s_AssetDatabase.GetBundleMap();
        }
        
        public static bool HasAsset(string assetName)
        {
            return s_AssetDatabase.HasAsset(assetName);
        }

        // 支持获取 AB 和 raw 文件的路径，返回本地路径或远程 url。若传入文件 id 不含 md5 后缀，则需包含 ext。
        public static string GetFilePath(string aliasName)
        {
            if (!string.IsNullOrEmpty(aliasName))
            {
                return s_AssetDatabase.GetFilePath(aliasName);                
            }

            return null;
        }
        
        public static void InstantiateAsync<T>(string assetName, Action<T> callback, Transform parent = null, bool irreversible = false) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(assetName))
            {
                Log.E("[Assets] assetName is null or empty!");
                return;
            }

            if (Umbreey.Inst)
            {
                Umbreey.Inst.StartCoroutine(InstantiateEnumerator(assetName, callback, parent ,irreversible));
            }
        }

        public static IEnumerator InstantiateEnumerator<T>(string assetName, Action<T> callback = null, Transform parent = null,  bool irreversible = false) where T : UnityEngine.Object
        {
            yield return InternalLoadAssetAsync<T>(assetName, (prefab) =>
            {
                try
                {
                    var go = UnityEngine.Object.Instantiate(prefab, parent != null ? parent : null);
                                    
                    if (UseAB)
                    {
                        InstObject instObject = new InstObject()
                        {
                            AssetObject = s_CacheAssetObject[assetName],
                            WeakRef = new WeakReference(go)
                        };
                        s_CacheInstObject.Add(instObject);
                    }

                    callback?.Invoke((T)go);
                }
                catch (Exception e)
                {
                    Log.E($"[Assets] Instantiate error: {e} {assetName}");
                }
            },
            irreversible);
        }
        
        public static void LoadAssetAsync<T>(string assetName, Action<T> callback, bool irreversible = false) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(assetName))
            {
                Log.E("[Assets] assetName is null or empty!");
                return;
            }
            
            if(typeof(T) == typeof(GameObject))
            {
                Log.E("[Assets] can not use LoadAssetAsync to load game object, must use InstantiateAsync instead!");
                return;
            }
            
            Umbreey.Inst.StartCoroutine(InternalLoadAssetAsync<T>(assetName, callback, irreversible));
        }
        
        public static IEnumerator LoadAssetEnumerator<T>(string assetName, Action<T> callback = null, bool irreversible = false) where T : UnityEngine.Object
        {
            yield return InternalLoadAssetAsync<T>(assetName, callback, irreversible);
        }
        
        public static void ReleaseAsset(string assetName)
        {
            if (UseAB)
            {
                if (!string.IsNullOrEmpty(assetName) && s_CacheAssetObject.TryGetValue(assetName, out var asset))
                {
                    asset.RefCount = Mathf.Max(0, asset.RefCount - 1);  
                }
            }
        }
        
        public static void LoadSceneAsync(string assetName, LoadSceneMode loadMode, Action<Scene> callback, Func<string, LoadSceneMode, bool> prepareCallback = null)
        {
            if (prepareCallback != null && !prepareCallback(assetName, loadMode))
            {
                return;
            }
            
#if UNITY_EDITOR
            if (s_AssetDatabase is AssetDatabaseEditor e)
            {
                Umbreey.Inst.StartCoroutine(e.LoadEditorSceneAsync(assetName, loadMode, callback));
                return;
            }
#endif
            
            Umbreey.Inst.StartCoroutine(LoadRuntimeSceneAsync(assetName, loadMode, callback));
        }
        
        public static void ReleaseScene(string assetName)
        {
            if (UseAB)
            {
                ReleaseAsset(assetName);
            }
        }

        public static void ClearPendingRequests(bool forceAll = false)
        {
            if (s_PendingAssetRequest.Count <= 0)
                return;

            var irreversibleRequests = new Dictionary<string, AssetCreateRequest>();
            foreach (var (assetName, r) in s_PendingAssetRequest)
            {
                if (!forceAll && r.Irreversible)
                {
                    irreversibleRequests.Add(assetName, r);
                }
            }
            
            s_PendingAssetRequest = irreversibleRequests;
        }
        
        public static void GC(Action callback = null)
        {
            Umbreey.Inst.StartCoroutine(InternalGC(callback));
        }

        private static IEnumerator InternalGC(Action callback)
        {
            for (int i = 0; i < 3; i++)
            {
                System.GC.Collect();
                yield return null;
                
                InternalGCAsset();
                yield return null;
                
                Resources.UnloadUnusedAssets();
                yield return null;
            }
            callback?.Invoke();
        }
        
        private static void InternalGCAsset()
        {
            if (!UseAB)
                return;

            int preAssetCount = s_CacheAssetObject.Count;
            int preBundleCount = s_AssetDatabase.GetCachedBundleCount();
            
            // gc assets
            Resources.UnloadUnusedAssets();
            for (int i = s_CacheInstObject.Count - 1; i >= 0; i--)
            {
                var instObject = s_CacheInstObject[i];
                if (!instObject.IsUsing)
                {
                    instObject.AssetObject.RefCount = Mathf.Max(0, instObject.AssetObject.RefCount - 1);
                    s_CacheInstObject.RemoveAt(i);
                }
            }

            // collect bundle in using & pending request
            HashSet<string> usingABNames = new HashSet<string>();
            void CollectUsingABNames(string ABName, string[] DependABNames)
            {
                usingABNames.Add(ABName);
                foreach (var depend in DependABNames)
                    usingABNames.Add(depend);
            }
            
            for (int i = s_CacheAssetObject.Count - 1; i >= 0; i--)
            {
                var item = s_CacheAssetObject.ElementAt(i);
                var assetObject = item.Value;
                
                if (assetObject.InUsing)
                    CollectUsingABNames(assetObject.ABName, assetObject.DependABNames);
                else
                    s_CacheAssetObject.Remove(item.Key);
            }
            
            foreach (var (assetName, _) in s_PendingAssetRequest)
            {
                var abName = s_AssetDatabase.GetBundleNames(assetName, out var dependNames);
                CollectUsingABNames(abName, dependNames);
            }
            
            // gc bundles
            s_AssetDatabase.UnloadUnusedBundles(usingABNames);
            Resources.UnloadUnusedAssets();
            
            Debug.Log($"GC. assets: {preAssetCount} -> {s_CacheAssetObject.Count}, bundles: {preBundleCount} -> {s_AssetDatabase.GetCachedBundleCount()}");
        }
        
        private static IEnumerator LoadRuntimeAssetAsync<T>(string assetName, Action<T> callback, bool irreversible) where T : UnityEngine.Object
        {
            AssetObject assetObject;
            if (s_CacheAssetObject.TryGetValue(assetName, out assetObject) && !assetObject.IsAliveType<T>())
            {
                s_CacheAssetObject.Remove(assetName);
            }
            
            if (!s_CacheAssetObject.TryGetValue(assetName, out assetObject))
            {
                // 譽譟･邇ｰ譛芽ｯｷ豎ゑｼ御ｻ･蜿頑弍蜷ｦ蜿ｯ謦､髞
                if (s_PendingAssetRequest.TryGetValue(assetName, out var exr))
                {
                    if (exr.Irreversible != irreversible)
                    {
                        Log.E($"[Assets] asset:{assetName} is already pending, but change irreversible: {exr.Irreversible} -> {irreversible}");
                    }
                }
                else
                {
                    s_PendingAssetRequest.Add(assetName, new AssetCreateRequest() { 
                        Request = null,
                        Irreversible = irreversible
                    });
                }
                
                // 开始加载资源
                try
                {
                    if (!s_AssetDatabase.TryGetBundleForAsset(assetName, out var bundleObject))
                    {
                        yield return Umbreey.Inst.StartCoroutine(s_AssetDatabase.LoadRuntimeBundleDependenciesAsync(assetName));                        
                    }

                    bool loadedAB = s_AssetDatabase.TryGetBundleForAsset(assetName, out bundleObject);
                    if (s_PendingAssetRequest.TryGetValue(assetName, out var r) && loadedAB)
                    {
                        if (r.Request != null)
                        {
                            while (!r.Request.isDone)
                                yield return null; // wait for exist request finish                            
                        }
                        else
                        {
                            r.Request = bundleObject.LoadAssetAsync<T>(assetName);
                            yield return r.Request;
                        }
                        
                        bool loaded = r.Request.asset != null;
                        if (!loaded)
                        {
                            var abName = s_AssetDatabase.GetBundleNames(assetName, out _);
                            Log.E($"[Assets] asset:{assetName} type:{typeof(T)} can't be loaded from ab:{abName}");
                        }
                        
                        if (!s_CacheAssetObject.TryGetValue(assetName, out assetObject))
                        {
                            bool still = s_PendingAssetRequest.TryGetValue(assetName, out var cur) && cur.Request == r.Request;
                            if (still && loaded)
                            {
                                var abName = s_AssetDatabase.GetBundleNames(assetName, out var dependNames);
                                assetObject = new AssetObject() 
                                {
                                    AssetName = assetName,
                                    ABName = abName,
                                    DependABNames = dependNames,
                                    WeakRef = new WeakReference(r.Request.asset as T)
                                };
                                s_CacheAssetObject[assetName] = assetObject;
                            }
                        }
                    }
                }
                finally
                {
                    s_PendingAssetRequest.Remove(assetName);                    
                }
            }

            if (assetObject != null)
            {
                assetObject.RefCount++;
                callback?.Invoke((T)assetObject.WeakRef.Target);                
            }
        }

        private static IEnumerator LoadRuntimeSceneAsync(string assetName, LoadSceneMode loadMode, System.Action<Scene> callback) 
        {
            AssetObject assetObject = null;
            
            string sceneName = Path.GetFileNameWithoutExtension(assetName);
            if (!s_CacheAssetObject.TryGetValue(assetName, out assetObject))
            {
                if (!s_AssetDatabase.TryGetBundleForAsset(assetName, out var bundleObject))
                {
                    yield return Umbreey.Inst.StartCoroutine(s_AssetDatabase.LoadRuntimeBundleDependenciesAsync(assetName));                        
                }
                
                var request = SceneManager.LoadSceneAsync(sceneName, loadMode);
                yield return request;
                
                if (!s_CacheAssetObject.TryGetValue(assetName, out assetObject))
                {
                    var abName = s_AssetDatabase.GetBundleNames(assetName, out var dependNames);
                    assetObject = new AssetObject()
                    {
                        AssetName = assetName,
                        ABName = abName,
                        DependABNames = dependNames,
                        WeakRef = null
                    };
                    s_CacheAssetObject[assetName] = assetObject;
                }
            }
            
            if (assetObject != null)
            {
                assetObject.RefCount++;
                callback?.Invoke(SceneManager.GetSceneByName(sceneName));
            }
        }
        
        private static IEnumerator InternalLoadAssetAsync<T>(string assetName, System.Action<T> callback, bool irreversible) where T : UnityEngine.Object
        {
#if UNITY_EDITOR
            if (s_AssetDatabase is AssetDatabaseEditor e)
            {
                if (Application.isPlaying)
                {
                    yield return e.LoadEditorAssetAsync<T>(assetName, callback);                
                }
                else
                {
                    e.LoadEditorAsset<T>(assetName, callback);
                }
                yield break;
            }
#endif
            yield return LoadRuntimeAssetAsync<T>(assetName, callback, irreversible);
        }
        
#if UNITY_EDITOR
        private const string EDTIOR_USE_AB = "Umbreey/Use AB";
        public static bool UseAB => Menu.GetChecked(EDTIOR_USE_AB);
        
        [MenuItem(EDTIOR_USE_AB)]
        private static void ToggleEditorUseAB()
        {
            bool useAB = Menu.GetChecked(EDTIOR_USE_AB);
            Menu.SetChecked(EDTIOR_USE_AB, !useAB);
        }
        
        static Assets()
        {
            s_AssetDatabase = new AssetDatabaseEditor();
        }
#else 
        public static bool UseAB => true;
#endif

        public static void Init(Action<bool> callback = null, string bundlemapUrl = null)
        {
#if UNITY_EDITOR
            if (UseAB && Application.isPlaying)
            {
                if (string.IsNullOrEmpty(bundlemapUrl))
                    EditorUtility.DisplayDialog("",$"Editor下正确使用本地AB资源\nStreamingAssets/{KBundleMapFileName}", "OK");
                    EditorUtility.DisplayDialog("",$"Editor下正确使用远程AB资源\n{bundlemapUrl}", "OK");
                    EditorUtility.DisplayDialog("",$"Editor下正确使用远程AB资源\n{bundlemapUrl}", "OK");
            }

            if (!UseAB)
            {
                s_AssetDatabase = new AssetDatabaseEditor();
            }
            else
#endif
            {
                if (string.IsNullOrEmpty(bundlemapUrl))
                    s_AssetDatabase = new AssetDatabaseLocal();
                else
                    s_AssetDatabase = new AssetDatabaseRemote(bundlemapUrl);
            }

            Umbreey.Inst.StartCoroutine(s_AssetDatabase.LoadRuntimeDatabase(callback));
        }
        
        public static void HotUpdate(BundleMap bundleMap)
        {
            if (s_AssetDatabase is AssetDatabaseLocal l)
            {
                l.HotUpdateRuntimeDatabase(bundleMap);
            }
        }
    }
}
