using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace Umbreey
{
    public class AssetDatabaseRemote : AssetDatabaseBase
    {
        private const int KTimeoutSeconds = 30;
        
        private string _sBundlemapUrl;
        private Dictionary<string, UnityWebRequest> s_PendingBundleRemoteRequest = new Dictionary<string, UnityWebRequest>();

        public AssetDatabaseRemote(string bundlemapUrl)
        {
            _sBundlemapUrl = bundlemapUrl;
        }
        
        public override string GetFilePath(string aliasName) 
        {
            string id = Path.GetFileNameWithoutExtension(aliasName); 
            var line = s_BundleMap.GetLineData(id);
            if (line == null)
            {
                Log.W($"[Assets] remote database can't find file. {aliasName}");
                return null;
            }

            if (string.IsNullOrEmpty(_sBundlemapUrl))
            {
                Log.W($"[Assets] remote head url is null. {aliasName}");
                return null;
            }
            
            var fileName = line.name;
            return $"{_sBundlemapUrl}/{fileName}";
        }
        
        public override IEnumerator LoadRuntimeDatabase(Action<bool> callback)
        {
            s_RuntimeDatabase?.Clear();
            if (string.IsNullOrEmpty(_sBundlemapUrl))
            {
                Log.E($"[Assets] remote bundle map url is empty.");
                yield break;
            }
            
            for (int i = 0; i < Assets.KRetryTimes; i++)
            {
                using UnityWebRequest request = UnityWebRequest.Get(_sBundlemapUrl);
                request.timeout = KTimeoutSeconds;
                yield return request.SendWebRequest();
                
                if (request.result == UnityWebRequest.Result.Success)
                {
                    BundleMap bundleMap = new BundleMap(request.downloadHandler.text);
                    SetRuntimeDatabase(bundleMap);
                    
                    var bundleMapName = Path.GetFileName(_sBundlemapUrl);
                    _sBundlemapUrl = _sBundlemapUrl.Replace($"/{bundleMapName}", "");
                    _sBundlemapUrl = _sBundlemapUrl.Replace($"\\{bundleMapName}", "");
                    
                    callback?.Invoke(true);
                    yield break;
                }
                
                Log.W($"[Assets] remote bundle map retry:{i+1}/{Assets.KRetryTimes}.. because {request.error}");
            }
            
            Log.E($"[Assets] remote bundle map failure after tried {Assets.KRetryTimes} times. url:{_sBundlemapUrl}");
            _sBundlemapUrl = null;
            
            callback?.Invoke(false);
        }

        protected override IEnumerator LoadRuntimeBundleAsync(string abName)
        {
            if (s_CacheBundleObject.ContainsKey(abName))
            {
                yield break;
            }

            AssetBundle bundleObject = null;
            if (!string.IsNullOrEmpty(_sBundlemapUrl))
            {
                string remotePath = $"{_sBundlemapUrl}/{abName}";
                if (s_PendingBundleRemoteRequest.TryGetValue(abName, out var remoteRequest))
                {
                    while (!remoteRequest.isDone)
                        yield return null; // wait for exist remote request finish
                }
                else
                {
                    remoteRequest = UnityWebRequest.Get(remotePath);
                    remoteRequest.timeout = KTimeoutSeconds;
                    remoteRequest.downloadHandler = new DownloadHandlerAssetBundle(remoteRequest.url, 0);
                    
                    s_PendingBundleRemoteRequest[abName] = remoteRequest;
                    yield return remoteRequest.SendWebRequest();
                    s_PendingBundleRemoteRequest.Remove(abName);
                    
                    if (remoteRequest.result != UnityWebRequest.Result.Success)
                    {
                        Log.E($"[Assets] remote bundle failure:{remoteRequest.error}. url:{remotePath}");
                    }    
                }
                
                bundleObject = (remoteRequest.downloadHandler as DownloadHandlerAssetBundle)?.assetBundle;
            }
            
            if (bundleObject != null && !s_CacheBundleObject.ContainsKey(abName))
            {
                s_CacheBundleObject[abName] = bundleObject;   
            }
        }


        
    }
}
