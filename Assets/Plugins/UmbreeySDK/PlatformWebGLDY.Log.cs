using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if UMBREEY_SDK_WEBGL_DY
using TTSDK;
#endif


namespace Umbreey
{
    // https://developer.open-douyin.com/docs/resource/zh-CN/mini-game/develop/guide/performance-optimization/performance-tuning-tool/real-time-log
    // https://developer.open-douyin.com/docs/resource/zh-CN/mini-game/develop/guide/performance-optimization/performance-tuning-tool/feedback-log-capability
    public partial class PlatformWebGLDY
    {
    #if UMBREEY_SDK_WEBGL_DY
        TTRealtimeLogManager _logManager;
    #endif
        
        public override void EnableRealtimeLog(bool enable)
        {
    #if UMBREEY_SDK_WEBGL_DY
            if (enable)
            {
                _logManager = TT.GetRealtimeLogManager(new GetRealtimeManagerParam() { SyncToConsole = false });
            }
            base.EnableRealtimeLog(enable);
    #endif
        }

        private bool IsTTDebugCallBridge(string str)
        {
            return str.StartsWith("DEBUG/[CallBridge]");
        }
		
        public override void I(string str)
        {
    #if UMBREEY_SDK_WEBGL_DY
            if (IsTTDebugCallBridge(str))
                return;
            
            _logManager.Info(str);
    #endif
        }
        
        public override void W(string str)
        {
    #if UMBREEY_SDK_WEBGL_DY
            if (IsTTDebugCallBridge(str))
                return;
				
            _logManager.Warn(str);
    #endif
        }
        
        public override void E(string str)
        {
    #if UMBREEY_SDK_WEBGL_DY
            if (IsTTDebugCallBridge(str))
                return;
				
            _logManager.Error(str);
    #endif
        }
 
        public override void AddFilterMsg(string tag)
        {
    #if UMBREEY_SDK_WEBGL_DY
            _logManager?.AddFilterMsg(tag);
    #endif
        }
        
        public override void SetFilterMsg(string tag)
        {
    #if UMBREEY_SDK_WEBGL_DY
            _logManager?.SetFilterMsg(tag);
    #endif
        }
    }
}
