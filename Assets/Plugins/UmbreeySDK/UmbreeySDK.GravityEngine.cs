using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

#if UMBREEY_SDK_GRAVITY_ENGINE
using GravityEngine;
using GravitySDK.PC.Constant;
using Umbreey;

// 数数SDK
public partial class UmbreeySDK
{
    public class GravityTimeTrack : IDisposable
    {
        private readonly string _eventName;
        private readonly Dictionary<string, object> _dict;

        public GravityTimeTrack(string eventName, Dictionary<string, object> dict)
        {
            _eventName = eventName;
            _dict = dict;

            GravityTimeForLaterTrack(eventName);
        }

        public void Dispose()
        {
            GravityTrack(_eventName, _dict);
        }
    }
    
    private class GravityInitializeCallback : GravityEngine.IInitializeCallback
    {
        public void onFailed(string errorMsg)
        {
            UmbreeySDK.LogError($"gravity engine initialize failed. errMsg:{errorMsg}");
        }

        public void onSuccess(Dictionary<string, object> responseJson)
        {
            var msg = responseJson?.ToString() ?? "";
            UmbreeySDK.Log($"gravity engine initialize success. json:{msg}");
           
            GravityEngineAPI.Flush();  // 建议在此执行一次Flush
        }
    }
    
    private class GravityDynamicCommonPropsHandler : GravityEngine.IDynamicSuperProperties
    {
        private Func<Dictionary<string, object>> _callback;
        public GravityDynamicCommonPropsHandler(Func<Dictionary<string, object>> callback)
        {
            _callback = callback;
        }
        
        public Dictionary<string, object> GetDynamicSuperProperties()
        {
            if (_callback != null)
            {
                return _callback?.Invoke();
            }
            return new Dictionary<string, object>();
        }
    }
    private static GravityDynamicCommonPropsHandler _gravityDynamicCommonPropsHandler;
    private static GameObject _gravityEngineInstance;
    
    public static void GravityInit(string accessToken, string channel ="base_channel", bool debugging = false)
    {
        if (_gravityEngineInstance != null)
        {
            UmbreeySDK.LogWarning("GravityEngine already initialized, skipping...");
            return;
        }
        _gravityEngineInstance = new GameObject("GravityEngine", typeof(GravityEngineAPI));
        Object.DontDestroyOnLoad(_gravityEngineInstance);
        
        var mode = debugging ? GravityEngineAPI.SDKRunMode.DEBUG : GravityEngineAPI.SDKRunMode.NORMAL;
        GravityEngineAPI.StartGravityEngine(accessToken, SystemInfo.deviceUniqueIdentifier, mode, channel);
        
    #if GRAVITY_WECHAT_GAME_MODE || GRAVITY_BILIBILI_GAME_MODE || GRAVITY_MEITUAN_GAME_MODE
        GravityEngineAPI.EnableAutoTrack(AUTO_TRACK_EVENTS.WECHAT_GAME_ALL);
        UmbreeySDK.Log("enable AUTO_TRACK_EVENTS.WECHAT_GAME_ALL");
    #elif GRAVITY_BYTEDANCE_GAME_MODE || GRAVITY_BYTEDANCE_TT_GAME_MODE
        GravityEngineAPI.EnableAutoTrack(AUTO_TRACK_EVENTS.BYTEDANCE_GAME_ALL);
		UmbreeySDK.Log("enable AUTO_TRACK_EVENTS.BYTEDANCE_GAME_ALL");
    #elif GRAVITY_KUAISHOU_GAME_MODE || GRAVITY_KUAISHOU_WEBGL_GAME_MODE
        GravityEngineAPI.EnableAutoTrack(AUTO_TRACK_EVENTS.KUAISHOU_GAME_ALL);
		UmbreeySDK.Log("enable AUTO_TRACK_EVENTS.KUAISHOU_GAME_ALL");
    #elif GRAVITY_ALIPAY_GAME_MODE
        GravityEngineAPI.EnableAutoTrack(AUTO_TRACK_EVENTS.ALIPAY_GAME_ALL);
		UmbreeySDK.Log("enable AUTO_TRACK_EVENTS.ALIPAY_GAME_ALL");
    #elif GRAVITY_OPPO_GAME_MODE || GRAVITY_HUAWEI_GAME_MODE || GRAVITY_XIAOMI_GAME_MODE
        GravityEngineAPI.EnableAutoTrack(AUTO_TRACK_EVENTS.QUICK_GAME_ALL);
		UmbreeySDK.Log("enable AUTO_TRACK_EVENTS.QUICK_GAME_ALL");
    #elif UNITY_ANDROID && !UNITY_EDITOR
        GravityEngineAPI.EnableAutoTrack(AUTO_TRACK_EVENTS.APP_ALL);
		UmbreeySDK.Log("enable AUTO_TRACK_EVENTS.APP_ALL");
    #elif UNITY_IOS && !UNITY_EDITOR
        GravityEngineAPI.EnableAutoTrack(AUTO_TRACK_EVENTS.APP_ALL);
		UmbreeySDK.Log("enable AUTO_TRACK_EVENTS.APP_ALL");
    #else
        // editor not auto track
    #endif

        OnGamePause(paused =>
        {
            if (paused) GravityFlush();
        });
    }

    public static void GravityPause(bool pause)
    {
        GravityEngineAPI.SetTrackStatus(pause ? GE_TRACK_STATUS.PAUSE : GE_TRACK_STATUS.NORMAL);
    }
    
    public static void GravityFlush()
    {
        GravityEngineAPI.Flush();
    }
    
    public static void GravityLogin(string id)
    {
        GravityEngineAPI.Login(id);
    }
    
    public static string GetGravityDistinctId()
    {
        return GravityEngineAPI.GetDistinctId();
    }

    public static void GravityUserPropsSet(Dictionary<string, object> dict)
    {
        GravityEngineAPI.UserSet(dict);
    }
    
    public static void GravityUserPropsSetOnce(Dictionary<string, object> dict)
    {
        GravityEngineAPI.UserSetOnce(dict);
    }
    
    public static void GravityUserPropsUnset(List<string> list)
    {
        GravityEngineAPI.UserUnset(list);
    }
    
    public static void GravityUserPropsAdd(Dictionary<string, object> dict)
    {
        GravityEngineAPI.UserAdd(dict);
    }
    
    public static void GravityCommonPropsSet(Dictionary<string, object> dict)
    {
        GravityEngineAPI.ClearSuperProperties();
        GravityEngineAPI.SetSuperProperties(dict);
    }
    
    public static void GravityDynamicCommonPropsSet(Func<Dictionary<string, object>> callback)
    {
        _gravityDynamicCommonPropsHandler = new GravityDynamicCommonPropsHandler(callback);
        GravityEngineAPI.SetDynamicSuperProperties(_gravityDynamicCommonPropsHandler);
    }

    public static void GravityTimeForLaterTrack(string name)
    {
        GravityEngineAPI.TimeEvent(name);
    }
    
    public static void GravityTrack(string name, Dictionary<string, object> dict)
    {
        GravityEngineAPI.Track(name, dict);
    }
    
    
    ///////////////////////////////////////////////////////////////////////////////////////////////////////////////
    // Only gravity engine
    ///////////////////////////////////////////////////////////////////////////////////////////////////////////////
    public static void GravityInitClient(string openId, string nickName, int version = 1)
    {
        GravityEngineAPI.Initialize(openId, nickName, version, openId, false, new GravityInitializeCallback());
    }

    //广告类型取值为：reward、banner、native、interstitial、video_feed、video_begin
    public static void GravityTrackAdShowEvent(string adType, string adUnitId, Dictionary<string, object> otherProperties = null)
    {
        if (Platform is PlatformWebGL)
        {
            if (Platform is PlatformWebGLWX)
            {
                GravityEngineAPI.TrackWechatAdShowEvent(adType, adUnitId, otherProperties);
            }
            else
            {
                GravityEngineAPI.TrackMiniGameAdShowEvent(adType, adUnitId, otherProperties);
            }
        }
        else
        {
            UmbreeySDK.Log($"skip gravity track ad show event, only webgl support. adType:{adType} adUnitId:{adUnitId}");
        }

    }
}

#endif // UMBREEY_SDK_GRAVITY_ENGINE
