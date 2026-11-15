using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

#if UNITY_EDITOR
using UnityEditor;

namespace Umbreey
{
    public class AssetDatabaseEditor : AssetDatabaseBase
    {
        private static Dictionary<string, string> s_EditorDatabase;
        private static HashSet<string> s_EditorCacheAssetObject = new HashSet<string>();
        
        public override string GetFilePath(string aliasName) 
        {
            if (s_EditorDatabase == null)
            {
                Log.W($"[Assets] editor database not initialized. {aliasName}");
                return null;
            }
            if (s_EditorDatabase.TryGetValue(aliasName, out var path))
            {
                return path;
            }

            Log.W($"[Assets] editor database can't find file. {aliasName}");
            return null;
        }
        
        public override IEnumerator LoadRuntimeDatabase(Action<bool> callback)
        {
            s_EditorDatabase ??= new Dictionary<string, string>();
            s_EditorDatabase.Clear();

            bool result;
            try
            {
                HashSet<string> packNames = new HashSet<string>();

                var guids = AssetDatabase.FindAssets("t:ABPack");
                for (int i = 0; i < guids.Length; i++)
                {
                    var guid = guids[i];
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var pack = AssetDatabase.LoadAssetAtPath<ABPack>(path);

                    if (packNames.Contains(pack.PackName))
                    {
                        Log.E($"[Assets] pack duplicated name {pack.PackName}. path:{path}");
                    }

                    packNames.Add(pack.PackName);

                    EditorUtility.DisplayProgressBar("提示", path, (float)i / (float)guids.Length);
                    foreach (var file in pack.GetFiles())
                    {
                        var assetName = Path.GetFileName(file);
                        if (s_EditorDatabase.TryGetValue(assetName, out var assetPath) && assetPath != file)
                        {
                            Log.E($"[Assets] asset duplicated name {assetName}");
                            EditorUtility.DisplayDialog("重要紧急！", $"{assetName}\n资源重名会导致打包失败，造成游戏加载崩溃等问题", "我马上修复");
                        }

                        s_EditorDatabase[assetName] = file;
                    }
                }

                result = true;
            }
            catch (Exception e)
            {
                result = false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            
            callback?.Invoke(result);
            yield break;
        }
        
        public override bool HasAsset(string assetName)
        {
            if (Assets.UseAB)
            {
                return base.HasAsset(assetName);
            }

            if (s_EditorDatabase == null)
                return false;
            
            return !string.IsNullOrEmpty(assetName) && s_EditorDatabase.ContainsKey(assetName);
        }
        
        public void LoadEditorAsset<T>(string name, Action<T> callback) where T : UnityEngine.Object
        {
            string assetPath = null;
            try
            {
                assetPath = s_EditorDatabase[name];
            }
            catch
            {
                string objName = (callback.Target is MonoBehaviour comp) ? comp.gameObject.GetScenePath() : "";
                Log.E($"[Assets] editor {name} not found. path:{objName}");
                return;
            }

            callback?.Invoke(AssetDatabase.LoadAssetAtPath<T>(assetPath));
        }
        
        public IEnumerator LoadEditorAssetAsync<T>(string assetName, Action<T> callback) where T : UnityEngine.Object
        {
            if (s_EditorDatabase == null)
            {
                yield return LoadRuntimeDatabase(null);
            }
            
            string assetPath = null;
            try
            {
                assetPath = s_EditorDatabase[assetName];
            }
            catch
            {
                string objName = (callback.Target is MonoBehaviour comp) ? comp.gameObject.GetScenePath() : "";
                Log.E($"[Assets] editor {assetName} not found. path:{objName}");
                yield break;
            }

            if (s_EditorCacheAssetObject.Add(assetPath))
            {
                yield return SimulateWaitAsync();
            }
            callback?.Invoke(AssetDatabase.LoadAssetAtPath<T>(assetPath));
        }
        
        public IEnumerator LoadEditorSceneAsync(string assetName, LoadSceneMode loadMode, System.Action<Scene> callback)
        {
            if (s_EditorDatabase == null)
            {
                yield return LoadRuntimeDatabase(null);
            }
            yield return SimulateWaitAsync();

            var scenePath = s_EditorDatabase[assetName];
            var request = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(scenePath,
                new LoadSceneParameters()
                {
                    loadSceneMode = loadMode
                });
            yield return request;
            
            string sceneName = Path.GetFileNameWithoutExtension(assetName);
            var sceneObject = SceneManager.GetSceneByName(sceneName);

            if (!sceneObject.isLoaded)
            {
                Log.E($"[Assets] editor scene {assetName} not loaded");
                yield break;
            }
            
            try
            {
                callback?.Invoke(sceneObject);
            }
            catch
            {
                Log.E($"[Assets] editor scene {assetName} loaded, but callback failed");
            }
        }

        private static IEnumerator SimulateWaitAsync()
        {
            int r = UnityEngine.Random.Range(1, 5);
            for (int i = 0; i < r; i++)
            {
                yield return null;
            }
        }

        
    #region Bundle Not Implemented
        protected override void SetRuntimeDatabase(BundleMap bundleMap)
        {
            throw new NotImplementedException();
        }
    
        protected override IEnumerator LoadRuntimeBundleAsync(string abName)
        {
            throw new NotImplementedException();
        }
            
        public override int GetCachedBundleCount()
        {
            throw new NotImplementedException();
        }
        
        public override string GetBundleNames(string assetName, out string[] dependABNames)
        {
            throw new NotImplementedException();
        }
            
        public override bool TryGetBundleForAsset(string assetName, out AssetBundle bundleObject)
        {
            throw new NotImplementedException();
        }
            
        public override void UnloadUnusedBundles(HashSet<string> usingNames)
        {
            throw new NotImplementedException();
        }
    #endregion
    }
}

#endif //UNITY_EDITOR
