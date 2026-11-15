using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if UMBREEY_SDK_WEBGL_WX
using WeChatWASM;
#endif

namespace Umbreey
{
    public partial class PlatformWebGLWX
    {
    #if UMBREEY_SDK_WEBGL_WX
        private uint _adHandleAlloc;
        private Dictionary<uint, (string id, WXCustomAd customAd)> _customAdMap = new Dictionary<uint, (string id, WXCustomAd customAd)>();
        private Dictionary<uint, (string id, WXRewardedVideoAd videoAd, bool showing)> _rewardVideoAdMap = new Dictionary<uint, (string, WXRewardedVideoAd, bool)>();
        
    #endif
        
        public override void DestroyAd(uint handle)
        {
    #if UMBREEY_SDK_WEBGL_WX
            if (_customAdMap.TryGetValue(handle, out var customIt))
            {
                var metaData = $"[{handle}] {customIt.id} ({customIt.customAd.instanceId})";
                try
                {
                    customIt.customAd.Destroy();
                    UmbreeySDK.Log($"WX.CustomAd destroy. {metaData}");
                }
                catch (Exception e)
                {
                    UmbreeySDK.LogError($"WX.CustomAd destroy. {metaData} exception:{e.Message}");
                }
                finally
                {
                    _customAdMap.Remove(handle);
                }
            }
            
            if (_rewardVideoAdMap.TryGetValue(handle, out var videoIt))
            {
                var metaData = $"[{handle}] {videoIt.id} ({videoIt.videoAd.instanceId})";
                try
                {
                    videoIt.videoAd.Destroy(); // !重要：确保不是单例模式创建的，multiton = true      
                    UmbreeySDK.Log($"WX.RewardedVideoAd destroy. {metaData}");
                }
                catch (Exception e)
                {
                    UmbreeySDK.LogError($"WX.RewardedVideoAd destroy. {metaData} exception:{e.Message}");
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
    #if UMBREEY_SDK_WEBGL_WX
            var videoAd = WX.CreateRewardedVideoAd(new WXCreateRewardedVideoAdParam() {
                adUnitId = id,
                multiton = true
            });
            uint adHandle = ++_adHandleAlloc;
            _rewardVideoAdMap.Add(adHandle, (id, videoAd, false));

            var metaData = $"[{adHandle}] {id} ({videoAd.instanceId})";
            UmbreeySDK.Log($"WX.RewardedVideoAd created. {metaData}");
            
            // 预加载
            videoAd.Load(rsp =>
            {
                UmbreeySDK.Log($"WX.RewardedVideoAd preload success. {metaData}");
                callback?.Invoke(UmbreeySDK.StatusCode.Success);                    
            }, rsp =>
            {
                if (_rewardVideoAdMap.ContainsKey(adHandle))
                {
                    UmbreeySDK.LogError($"WX.RewardedVideoAd preload failure. {metaData} errCode:{rsp.errCode} {rsp.errMsg}");
                    callback?.Invoke(UmbreeySDK.StatusCode.Failure);
                }
                else
                {
                    UmbreeySDK.Log($"WX.RewardedVideoAd preload abort because destroyed. {metaData} errCode:{rsp.errCode} {rsp.errMsg}");
                }
            });

            return adHandle;
    #endif
            return 0;
        }

        public override uint ShowRewardVideoAd(RewardVideoAdParam param, Action<UmbreeySDK.StatusCode> callback)
        {
    #if UMBREEY_SDK_WEBGL_WX
            uint adHandle = 0;
            WXRewardedVideoAd videoAd = null;
            
            // 如果有预创建的广告，则直接使用
            if (_rewardVideoAdMap.TryGetValue(param.Handle, out var item))
            {
                adHandle = param.Handle;
                videoAd = item.videoAd;

                if (item.showing)
                {
                    UmbreeySDK.LogWarning($"WX.RewardedVideoAd is playing so skip show. [{adHandle}] {param.Id} ({videoAd.instanceId})");
                    callback?.Invoke(UmbreeySDK.StatusCode.Cancel);
                    return adHandle;
                }
            }
            else
            {
                videoAd = WX.CreateRewardedVideoAd(new WXCreateRewardedVideoAdParam() {
                    adUnitId = param.Id,
                    multiton = true
                });
                
                adHandle = ++_adHandleAlloc;
                _rewardVideoAdMap.Add(adHandle, (param.Id, videoAd, false));
                
                UmbreeySDK.Log($"WX.RewardedVideoAd not preloaded for handle {param.Handle}, so create a new one. [{adHandle}] {param.Id} (instanceId {videoAd.instanceId}): {param.BranchId} {param.BranchDim}");
            }
            
            // 显示广告
            var metaData = $"[{adHandle}] {param.Id} ({videoAd.instanceId}) branch:{param.BranchId} ({param.BranchDim})";
            void OnCloseComplete(WXRewardedVideoAdOnCloseResponse rsp)
            {
                UmbreeySDK.Log($"WX.RewardedVideoAd OnClose {metaData} {rsp.isEnded} {rsp.errMsg}");
                videoAd.OffClose(OnCloseComplete); // 避免同个广告多次show时触发多个onClose回调
                _rewardVideoAdMap[adHandle] = (param.Id, videoAd, false);
                
                callback?.Invoke(rsp is { isEnded: true } ? UmbreeySDK.StatusCode.Success : UmbreeySDK.StatusCode.Cancel);                
            }
            videoAd.OnClose(OnCloseComplete);
            
            void OnShowSuccess(WXTextResponse rsp)
            {
                UmbreeySDK.Log($"WX.RewardedVideoAd show success. {metaData}");
            }
            
            void OnShowFailure(WXTextResponse rsp)
            {
                UmbreeySDK.LogError($"WX.RewardedVideoAd show failure. {metaData} errCode:{rsp.errCode} {rsp.errMsg}");
                videoAd.Load(loadRsp =>
                {
                    videoAd.Show(data2 =>
                    {
                        UmbreeySDK.Log($"WX.RewardedVideoAd retry show success. {metaData}");
                    }, rsp2 =>
                    {
                        UmbreeySDK.LogError($"WX.RewardedVideoAd retry show failure. {metaData} errCode:{rsp2.errCode} {rsp2.errMsg}");
                        callback?.Invoke(UmbreeySDK.StatusCode.Failure);
                    });
                }, loadRsp =>
                {
                    UmbreeySDK.LogError($"WX.RewardedVideoAd retry load failure. {metaData} errCode:{loadRsp.errCode} {loadRsp.errMsg}");
                    callback?.Invoke(UmbreeySDK.StatusCode.Failure);
                });                
            }
            
            _rewardVideoAdMap[adHandle] = (param.Id, videoAd, true);
            if (string.IsNullOrEmpty(param.BranchId))
            {
                videoAd.Show(OnShowSuccess, OnShowFailure);                
            }
            else
            {
                videoAd.Show(param.BranchId, param.BranchDim, OnShowSuccess, OnShowFailure);
            }

            return adHandle;
#endif
            return 0;
        }
        
        public override uint ShowCustomAd(CustomAdParam param, Action<UmbreeySDK.StatusCode> callback)
        {
    #if UMBREEY_SDK_WEBGL_WX
            var rect = GetCustomAdRect(param, Vector2.zero);
            if (rect is { width:0, height:0 })
            {
                callback?.Invoke(UmbreeySDK.StatusCode.Fallback);
                return 0;
            }

            var option = new WXCreateCustomAdParam() {
                adUnitId = param.Id, 
                adIntervals = 30,
                style = new CustomStyle()
                {
                    left = rect.x,
                    top = rect.y,
                    width = rect.width,
                }
            };

            // show
            uint adHandle = ++_adHandleAlloc;
            var customAd = WX.CreateCustomAd(option);
            _customAdMap.Add(adHandle, (param.Id, customAd));
            
            var metaData = $"[{adHandle}] {param.Id} ({customAd.instanceId}) {param.Type} pivot:({param.PivotX}, {param.PivotY} rect:({rect.x}, {rect.y}, {rect.width}, {rect.height})";
            UmbreeySDK.Log($"WX.CustomAd created. {metaData}");
            
            void OnShowSuccess(WXTextResponse rsp)
            {
                UmbreeySDK.Log($"WX.CustomAd show success. {metaData}");
                callback?.Invoke(UmbreeySDK.StatusCode.Success);
            }
            void OnShowFailure(WXTextResponse rsp)
            {
                UmbreeySDK.LogError($"WX.CustomAd show failure. {metaData} errCode:{rsp.errCode} {rsp.errMsg}");
                callback?.Invoke(UmbreeySDK.StatusCode.Failure);
            }

            if (string.IsNullOrEmpty(param.BranchId))
            {
                customAd.Show(OnShowSuccess, OnShowFailure);
            }
            else
            {
                customAd.Show(param.BranchId, param.BranchDim, OnShowSuccess, OnShowFailure);
            }
            
            return adHandle;
#endif
            return 0;
        }
    }
}
