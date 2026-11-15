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
        private WXRealtimeLogManager _logManager;
    #endif
        
        public override void EnableRealtimeLog(bool enable)
        {
    #if UMBREEY_SDK_WEBGL_WX
            if (enable)
            {
                _logManager = WX.GetRealtimeLogManager();
            }
            base.EnableRealtimeLog(enable);
    #endif
        }
        
        public override void I(string str)
        {
    #if UMBREEY_SDK_WEBGL_WX
            _logManager.Info(new string[] {str});
    #endif
        }
        
        public override void W(string str)
        {
    #if UMBREEY_SDK_WEBGL_WX
            _logManager.Warn(new string[] {str});
    #endif
        }
        
        public override void E(string str)
        {
    #if UMBREEY_SDK_WEBGL_WX
            _logManager.Error(new string[] {str});
    #endif
        }
 
        public override void AddFilterMsg(string tag)
        {
    #if UMBREEY_SDK_WEBGL_WX
            _logManager?.AddFilterMsg(tag);
    #endif
        }
        
        public override void SetFilterMsg(string tag)
        {
    #if UMBREEY_SDK_WEBGL_WX
            _logManager?.SetFilterMsg(tag);
    #endif
        }
        
    }
}
