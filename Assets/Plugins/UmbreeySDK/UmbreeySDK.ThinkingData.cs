using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if UMBREEY_SDK_THINKING_ANALYTICS
using System;
using ThinkingData.Analytics;

// 数数SDK
public partial class UmbreeySDK
{
    public class ThinkingTimeTrack : IDisposable
    {
        private readonly string _eventName;
        private readonly Dictionary<string, object> _dict;

        public ThinkingTimeTrack(string eventName, Dictionary<string, object> dict)
        {
            _eventName = eventName;
            _dict = dict;

            ThinkingTimeForLaterTrack(eventName);
        }

        public void Dispose()
        {
            ThinkingTrack(_eventName, _dict);
        }
    }
    
    private class ThinkingDynamicCommonPropsHandler : ThinkingData.Analytics.TDDynamicSuperPropertiesHandler
    {
        private Func<Dictionary<string, object>> _callback;
        public ThinkingDynamicCommonPropsHandler(Func<Dictionary<string, object>> callback)
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
    private static ThinkingDynamicCommonPropsHandler _thinkingDynamicCommonPropsHandler;
    
    public static void ThinkingInit(string appId, string serverUrl, bool debugging = false)
    {
        TDAnalytics.Init(new TDConfig(appId, serverUrl)
        {
            mode = debugging ? TDMode.Debug : TDMode.Normal
        });
		
        var eventType = debugging ? TDAutoTrackEventType.All : 
            TDAutoTrackEventType.AppInstall | TDAutoTrackEventType.AppStart | TDAutoTrackEventType.AppEnd | TDAutoTrackEventType.AppCrash;
        
        TDAnalytics.EnableLog(debugging);
        TDAnalytics.EnableAutoTrack(eventType);

        OnGamePause(paused =>
        {
            if (paused) ThinkingFlush();
        });
    }

    public static void ThinkingPause(bool pause)
    {
        TDAnalytics.SetTrackStatus(pause ? TDTrackStatus.Pause : TDTrackStatus.Normal);
    }
    public static void ThinkingFlush()
    {
        TDAnalytics.Flush();
    }
    
    public static void ThinkingLogin(string id)
    {
        TDAnalytics.Login(id);
    }
    
    public static void ThinkingLogout()
    {
        TDAnalytics.Logout();
    }
    
    public static string GetDistinctId()
    {
        return TDAnalytics.GetDistinctId();
    }

    public static void SetDistinctId(string id)
    {
        TDAnalytics.SetDistinctId(id);
    }

    public static void ThinkingUserPropsSet(Dictionary<string, object> dict)
    {
        TDAnalytics.UserSet(dict);
    }
    
    public static void ThinkingUserPropsSetOnce(Dictionary<string, object> dict)
    {
        TDAnalytics.UserSetOnce(dict);
    }
    
    public static void ThinkingUserPropsUnset(List<string> list)
    {
        TDAnalytics.UserUnset(list);
    }
    
    public static void ThinkingUserPropsAdd(Dictionary<string, object> dict)
    {
        TDAnalytics.UserAdd(dict);
    }
    
    public static void ThinkingCommonPropsSet(Dictionary<string, object> dict)
    {
        TDAnalytics.ClearSuperProperties();
        TDAnalytics.SetSuperProperties(dict);
    }
    
    public static void ThinkingDynamicCommonPropsSet(Func<Dictionary<string, object>> callback)
    {
        _thinkingDynamicCommonPropsHandler = new ThinkingDynamicCommonPropsHandler(callback);
        TDAnalytics.SetDynamicSuperProperties(_thinkingDynamicCommonPropsHandler);
    }

    public static void ThinkingTimeForLaterTrack(string name)
    {
        TDAnalytics.TimeEvent(name);
    }
    
    public static void ThinkingTrack(string name, Dictionary<string, object> dict)
    {
        TDAnalytics.Track(name, dict);
    }
    
    public static void ThinkingTrackOnce(string name, Dictionary<string, object> dict, string firstCheckUserprop)
    {
        TDFirstEventModel model = string.IsNullOrEmpty(firstCheckUserprop) ?
            new TDFirstEventModel(name) : new TDFirstEventModel(name, firstCheckUserprop);
        
        model.Properties = dict;
        TDAnalytics.Track(model);
    }
    
}

#endif // UMBREEY_SDK_THINKING_ANALYTICS
