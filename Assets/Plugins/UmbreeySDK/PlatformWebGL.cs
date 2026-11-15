using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Umbreey
{
    public partial class PlatformWebGL : PlatformBase
    {
        public override string OnKeyOperatorType()
        {
            UmbreeySDK.LogWarning("OnKeyOperatorType is not supported on default WebGL platform.");
            return string.Empty;
        }
        
        internal override void Init(Action<UmbreeySDK.StatusCode> callback, params string[] param)
        {
            callback?.Invoke(UmbreeySDK.StatusCode.Success);
        }

        // login
        internal override void Login(Action<UmbreeySDK.StatusCode, UmbreeySDK.LoginData> callback, params string[] param)
        {
            callback?.Invoke(UmbreeySDK.StatusCode.Success, new UmbreeySDK.LoginData()
            {
                Name = SystemInfo.deviceUniqueIdentifier
            });
        }
        
        internal override void Logout(Action<UmbreeySDK.StatusCode> callback, params string[] param)
        {
            UmbreeySDK.LogWarning("Logout is not supported on default WebGL platform.");
        }
        
        internal override bool SupportsLogout()
        {
            UmbreeySDK.LogWarning("SupportsLogout is not supported on default WebGL platform.");
            return false;
        }

        // pay
        internal override void Pay(Action<UmbreeySDK.StatusCode> callback, params string[] param)
        {
            UmbreeySDK.LogWarning("Pay is not supported on default WebGL platform.");
        }

        internal override void Pay2(Action<UmbreeySDK.StatusCode> callback, params string[] param)
        {
            UmbreeySDK.LogWarning("Pay2 is not supported on default WebGL platform.");
        }

        // share
        internal override void Share(Action<UmbreeySDK.StatusCode> callback, UmbreeySDK.ShareParams param)
        {
            UmbreeySDK.LogWarning("Sharing is not supported on default WebGL platform.");
        }

        // custom
        internal override string GetCustomData(params string[] param)
        {
            UmbreeySDK.LogWarning("Get Custom data is not supported on default WebGL platform.");
            return null;
        }

        internal override string SetCustomData(params string[] param)
        {
            UmbreeySDK.LogWarning("Set custom data is not supported on default WebGL platform.");
            return null;
        }

        // utils
        internal override long GetAvailableFreeSpace()
        {
            UmbreeySDK.LogWarning("Get available free space is not supported on default WebGL platform.");
            return long.MaxValue;
        }

        internal override void OpenUrl(string url)
        {
            UmbreeySDK.LogWarning("Open URL is not supported on default WebGL platform.");
        }

        internal override void Vibrate()
        {
            UmbreeySDK.LogWarning("Vibrate is not supported on default WebGL platform.");
        }
        
        internal override void SaveImageToPhotoAlbum(Action<UmbreeySDK.StatusCode> callback, Texture2D texture, string filename, string album)
        {
            UmbreeySDK.LogWarning("SaveImageToPhotoAlbum is not supported on default WebGL platform.");
        }
        
        //////////////////////////////////////////////////////////////////////////////////////////////////////////
        /// Below is the code for webgl platform only
        //////////////////////////////////////////////////////////////////////////////////////////////////////////
        protected static string FilterDeviceModel(string model)
        {
            int startIndex = model.IndexOf('<') + 1;
            int endIndex = model.IndexOf('>');
            if (startIndex > 0 && endIndex > startIndex)
            {
                var m = model.Substring(startIndex, endIndex - startIndex);
                UmbreeySDK.Log($"filter model: {model} -> {m}");
                
                return m;
            }
            
            return model;
        }
        
        public struct UserData
        {
            public string NickName;
            public string AvatarUrl;
            public int Gender;
        }

        public virtual void CreateUserInfoButton(Action<UmbreeySDK.StatusCode, UserData> callback, UnityEngine.UI.Button button, Camera uiCamera = null) // 按钮为空表示全屏区域
        {
            UmbreeySDK.LogWarning("BindUserInfoButton is not supported on default WebGL platform");
            callback?.Invoke(UmbreeySDK.StatusCode.Failure, new UserData());
        }
        
        public virtual void GetUserInfo(Action<UmbreeySDK.StatusCode, UserData> callback, bool openSetting = false)
        {
            UmbreeySDK.LogWarning("GetUserInfo is not supported on default WebGL platform");
            callback?.Invoke(UmbreeySDK.StatusCode.Failure, new UserData());
        }
        
        public class LaunchOptions
        {
            public string scene;
            public Dictionary<string, string> query;
        }
        
        public virtual LaunchOptions GetLaunchOptions()
        {
            return new LaunchOptions() {                
                scene = "",
                query = new Dictionary<string, string>(),
            };
        }

        public struct TouchInfo
        {
            public int identifier;
            public float x;
            public float y;
        }

        public virtual void OnTouchEnd(Action<TouchInfo> onTouchEnd)
        {
            UmbreeySDK.LogWarning("OnTouchEnd is not supported on default WebGL platform.");
        }

        public virtual void OffTouchEnd(Action<TouchInfo> onTouchEnd)
        {
            UmbreeySDK.LogWarning("OffTouchEnd is not supported on default WebGL platform.");
        }

        public virtual void RequestSubscribeMessage(List<string> templateIds, Action<Dictionary<string, string>> callback)
        {
            UmbreeySDK.LogWarning("RequestSubscribeMessage is not supported on default WebGL platform.");
            callback?.Invoke(new Dictionary<string, string>());
        }
        
        public virtual void SetPreferredFramesPerSecond(int fps)
        {
            UmbreeySDK.LogWarning("SetPreferredFramesPerSecond is not supported on default WebGL platform.");
        }

        public virtual void SetDevicePixelRatio(float dpr)
        {
            UmbreeySDK.LogWarning("SetDevicePixelRatio is not supported on default WebGL platform.");
        }

        public virtual float GetDevicePixelRatio()
        {
            UmbreeySDK.LogWarning("GetDevicePixelRatio is not supported on default WebGL platform.");
            return 1.0f;
        }
        
        public enum CustomAdType : int
        {
            Unknown = 0,
            Banner,
            Cell,
        }
        
        public class CustomAdParam
        {
            public CustomAdType Type;
            public string Id;
            
            // 屏幕左下角为原点
            public float PivotX = 0.5f; 
            public float PivotY = 0.5f;
            
            // 微信用于场景数据分析
            public string BranchId;
            public string BranchDim = "";
        }
        
        public class RewardVideoAdParam
        {
            public uint Handle; // 预创建的广告句柄
            public string Id;
            
            // 微信用于场景数据分析
            public string BranchId;
            public string BranchDim = "";
        }

        public virtual void DestroyAd(uint handle)
        {
            UmbreeySDK.LogWarning("DestroyAd is not supported on default WebGL platform.");
        }

        public virtual uint CreateRewardVideoAd(string id, Action<UmbreeySDK.StatusCode> callback = null)
        {
            UmbreeySDK.LogWarning("CreateRewardVideoAd is not supported on default WebGL platform.");
            callback?.Invoke(UmbreeySDK.StatusCode.Fallback);
            return 0;
        }

        public virtual uint ShowRewardVideoAd(RewardVideoAdParam param, Action<UmbreeySDK.StatusCode> callback)
        {
            UmbreeySDK.LogWarning("ShowRewardVideoAd is not supported on default WebGL platform.");
            callback?.Invoke(UmbreeySDK.StatusCode.Fallback);
            return 0;
        }

        public virtual uint ShowCustomAd(CustomAdParam param, Action<UmbreeySDK.StatusCode> callback)
        {
            UmbreeySDK.LogWarning("ShowCustomAd is not supported on default WebGL platform.");
            callback?.Invoke(UmbreeySDK.StatusCode.Fallback);
            return 0;
        }
        
        protected RectInt GetCustomAdRect(CustomAdParam param, Vector2 adSize)
        {
            // 允许外部指定广告大小，如果没有指定就使用默认值
            if (adSize is { x: 0, y: 0 })
            {
                // 每种模板广告大小见：https://ad.weixin.qq.com/pdf.html?post_id=U2FsdGVkX1+ZTD+suHBEHadR+vmX9KTCrIYmLXErm/k=
                switch (param.Type)
                {
                    case CustomAdType.Banner:
                        adSize = new Vector2(375, 120);
                        break;
                    case CustomAdType.Cell:
                        adSize = new Vector2(65, 105); // 考虑常规(60, 104)和卡片(68, 106)
                        break;
                    default:
                        UmbreeySDK.LogError($"CustomAd {param.Type} not supported.");
                        return new RectInt(0, 0, 0, 0);
                }                
            }
            
            var screenArea = GetScreenSafeArea();
            var adCenter = new Vector2(
                screenArea.width * Mathf.Clamp(param.PivotX, 0, 1f),
                screenArea.height * Mathf.Clamp(1.0f - param.PivotY, 0, 1f)); // UGUI是左下角原点，但微信上是左上角
            
            var rect = new RectInt() {
                x = (int)(adCenter.x - adSize.x / 2), 
                y = (int)(adCenter.y - adSize.y / 2), 
                width = (int)adSize.x,
                height = (int)adSize.y
            };

            return rect;
        }
        
        public virtual void OnShareMenu(Action<UmbreeySDK.StatusCode> callback, Func<UmbreeySDK.ShareParams> getShareParam)
        {
            UmbreeySDK.LogWarning("OnShareMenu is not supported on default WebGL platform.");
        }

        public virtual void ShowShareMenu(bool showAppMessage, bool showTimeline)
        {
            UmbreeySDK.LogWarning("ShowShareMenu is not supported on default WebGL platform.");
        }
        
    }
    
}
