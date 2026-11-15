using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if UMBREEY_SDK_WEBGL_DY
using TTSDK;
using TTSDK.UNBridgeLib.LitJson;
#endif

namespace Umbreey
{
    public partial class PlatformWebGLDY
    {
    #if UMBREEY_SDK_WEBGL_DY
        private uint _adHandleAlloc;
        private Dictionary<uint, (string id, TTBannerAd customAd)> _customAdMap = new Dictionary<uint, (string id, TTBannerAd customAd)>();
        private Dictionary<uint, (string id, TTRewardedVideoAd videoAd, bool showing)> _rewardVideoAdMap = new Dictionary<uint, (string, TTRewardedVideoAd, bool)>();
        
    #endif
        
        public override void DestroyAd(uint handle)
        {
    #if UMBREEY_SDK_WEBGL_DY
            if (_customAdMap.TryGetValue(handle, out var customIt))
            {
                var metaData = $"[{handle}] {customIt.id} ({customIt.customAd.GetHashCode()})";
                try
                {
                    customIt.customAd.Destroy();
                    UmbreeySDK.Log($"TT.CustomAd destroy. {metaData}");
                }
                catch (Exception e)
                {
                    UmbreeySDK.LogError($"TT.CustomAd destroy. {metaData} exception:{e.Message}");
                }
                finally
                {
                    _customAdMap.Remove(handle);
                }
            }
            
            if (_rewardVideoAdMap.TryGetValue(handle, out var videoIt))
            {
                var metaData = $"[{handle}] {videoIt.id} ({videoIt.videoAd.GetHashCode()})";
                try
                {
                    videoIt.videoAd.Destroy(); // !重要：确保不是单例模式创建的，multiton = true      
                    UmbreeySDK.Log($"TT.RewardedVideoAd destroy. {metaData}");
                }
                catch (Exception e)
                {
                    UmbreeySDK.LogError($"TT.RewardedVideoAd destroy. {metaData} exception:{e.Message}");
                }
                finally
                {
                    _rewardVideoAdMap.Remove(handle);
                }
            }
    #endif
        }
        
        // 创建激励视频广告并进行预加载
        public override uint CreateRewardVideoAd(string id, Action<UmbreeySDK.StatusCode> callback = null)
        {
    #if UMBREEY_SDK_WEBGL_DY
            var videoAd = TT.CreateRewardedVideoAd(new CreateRewardedVideoAdParam() {
                AdUnitId = id
            });
            uint adHandle = ++_adHandleAlloc;
            _rewardVideoAdMap.Add(adHandle, (id, videoAd, false));

            var metaData = $"[{adHandle}] {id} ({videoAd.GetHashCode()})";
            UmbreeySDK.Log($"TT.RewardedVideoAd created. {metaData}");
            
            // 预加载
            void OnLoadSuccess()
            {
                UmbreeySDK.Log($"TT.RewardedVideoAd preload success. {metaData}");

                videoAd.OnLoad -= OnLoadSuccess;
                callback?.Invoke(UmbreeySDK.StatusCode.Success); 
            }
            
            void OnLoadFailure(int errCode, string errMsg)
            {
                UmbreeySDK.LogError($"TT.RewardedVideoAd preload failure. {metaData} errCode:{errCode} {errMsg}");

                videoAd.OnError -= OnLoadFailure;
                if (_rewardVideoAdMap.ContainsKey(adHandle))
                {
                    callback?.Invoke(UmbreeySDK.StatusCode.Failure);
                }
                else
                {
                    UmbreeySDK.Log($"TT.RewardedVideoAd preload abort because destroyed. {metaData} errCode:{errCode} {errMsg}");
                }
            }
            videoAd.OnLoad += OnLoadSuccess;
            videoAd.OnError += OnLoadFailure;
            videoAd.Load();

            return adHandle;
    #endif
            return 0;
        }

        public override uint ShowRewardVideoAd(RewardVideoAdParam param, Action<UmbreeySDK.StatusCode> callback)
        {
    #if UMBREEY_SDK_WEBGL_DY
            uint adHandle = 0;
            TTRewardedVideoAd videoAd = null;
            
            // 如果有预创建的广告，则直接使用
            if (_rewardVideoAdMap.TryGetValue(param.Handle, out var item))
            {
                adHandle = param.Handle;
                videoAd = item.videoAd;

                if (item.showing)
                {
                    UmbreeySDK.LogWarning($"TT.RewardedVideoAd is playing so skip show. [{adHandle}] {param.Id} ({videoAd.GetHashCode()})");
                    callback?.Invoke(UmbreeySDK.StatusCode.Cancel);
                    return adHandle;
                }
            }
            else
            {
                videoAd = TT.CreateRewardedVideoAd(new CreateRewardedVideoAdParam() {
                    AdUnitId = param.Id
                });
                
                adHandle = ++_adHandleAlloc;
                _rewardVideoAdMap.Add(adHandle, (param.Id, videoAd, false));
                
                UmbreeySDK.Log($"TT.RewardedVideoAd not preloaded for handle {param.Handle}, so create a new one. [{adHandle}] {param.Id} (instanceId {videoAd.GetHashCode()}): {param.BranchId} {param.BranchDim}");
            }
            
            // 显示广告
            var metaData = $"[{adHandle}] {param.Id} ({videoAd.GetHashCode()})";
            void OnCloseComplete(bool isEnded, int count)
            {
                UmbreeySDK.Log($"TT.RewardedVideoAd OnClose {metaData} {isEnded} {count}");
                videoAd.OnClose -= OnCloseComplete; // 避免同个广告多次show时触发多个onClose回调
                videoAd.OnError -= OnShowFailure;
                _rewardVideoAdMap[adHandle] = (param.Id, videoAd, false);
                
                callback?.Invoke(isEnded ? UmbreeySDK.StatusCode.Success : UmbreeySDK.StatusCode.Cancel);                
            }
            
            void OnShowFailure(int errCode, string errMsg)
            {
                UmbreeySDK.LogError($"TT.RewardedVideoAd show failure. {metaData} errCode:{errCode} {errMsg}");
                videoAd.OnClose -= OnCloseComplete; // 避免同个广告多次show时触发多个onClose回调
                videoAd.OnError -= OnShowFailure;
                callback?.Invoke(UmbreeySDK.StatusCode.Failure);
            }
            
            _rewardVideoAdMap[adHandle] = (param.Id, videoAd, true);
            videoAd.OnClose += OnCloseComplete;
            videoAd.OnError += OnShowFailure;
            videoAd.Show();                
            
            return adHandle;
#endif
            return 0;
        }
        
        public override uint ShowCustomAd(CustomAdParam param, Action<UmbreeySDK.StatusCode> callback)
        {
    #if UMBREEY_SDK_WEBGL_DY
            var rect = GetCustomAdRect(param, Vector2.zero);
            if (rect is { width:0, height:0 })
            {
                callback?.Invoke(UmbreeySDK.StatusCode.Fallback);
                return 0;
            }
            
            var style = new TTBannerStyle() {
                left = rect.x,
                top = rect.y,
                width = rect.width,
            };
            
            // show
            uint adHandle = ++_adHandleAlloc;
            var customAd = TT.CreateBannerAd(param.Id, style);
            _customAdMap.Add(adHandle, (param.Id, customAd));
            
            var metaData = $"[{adHandle}] {param.Id} ({customAd.GetHashCode()}) {param.Type} pivot:({param.PivotX}, {param.PivotY} rect:({rect.x}, {rect.y}, {rect.width}, {rect.height})";
            UmbreeySDK.Log($"TT.CustomAd created. {metaData}");
            
            void OnLoadSuccess()
            {
                UmbreeySDK.Log($"TT.CustomAd show success. {metaData}");
                customAd.OnLoad -= OnLoadSuccess; 
                customAd.OnError -= OnShowFailure;
                customAd.Show(); // 必须在onLoad回调后调用show，否则会报错
                callback?.Invoke(UmbreeySDK.StatusCode.Success);
            }
            void OnShowFailure(int errCode, string errMsg)
            {
                UmbreeySDK.LogError($"TT.CustomAd show failure. {metaData} errCode:{errCode} {errMsg}");
                customAd.OnLoad -= OnLoadSuccess; 
                customAd.OnError -= OnShowFailure;
                callback?.Invoke(UmbreeySDK.StatusCode.Failure);
            }

            customAd.OnLoad += OnLoadSuccess;
            customAd.OnError += OnShowFailure;
            
            return adHandle;
#endif
            return 0;
        }
    }
}
