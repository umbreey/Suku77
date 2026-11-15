#if UMBREEY_SDK_WEBGL_WX && ENABLE_WX_PERF_FEATURE
    #define UMBREEY_SDK_WEBGL_WX_PERF
#endif

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;


#if UMBREEY_SDK_WEBGL_WX
using WeChatWASM;
using WXSDKPerf;
#endif


namespace Umbreey
{
    public partial class PlatformWebGLWX
    {
        public static void Annotation(string name, bool snapshot = false)
        {
    #if UMBREEY_SDK_WEBGL_WX_PERF
            var title = snapshot ? $"{name}_CaptureUnityMemorySnapshot" : name;
            WXPerfEngine.Annotation(title);
    #endif
        }
        
        public static bool IsRecording()
        {
    #if UMBREEY_SDK_WEBGL_WX_PERF
            return WXPerfEngine.IsRecording();
    #else
            return false;
    #endif
        }
        
        /// interpType, 0:不插值. 1:线性插值；2:Step插值
        public static void DeclareCustomStatInfo(string name, string category, int interpType = 0)
        {
    #if UMBREEY_SDK_WEBGL_WX_PERF
            WXPerfEngine.DeclareCustomStatInfo(name, category, interpType);
    #endif
        }

        public static void SetCustomStatValue(string name, float value)
        {
    #if UMBREEY_SDK_WEBGL_WX_PERF
            WXPerfEngine.SetCustomStatValue(name, value);
    #endif
        }

        // 如果未进行指标声明，将自动声明该指标，该指标将出现在报告的“Project Default Stat Category”
        // 每次调用将value累加到指标统计
        public static void AddCustomStatInfoBy(string name, float value)
        {    
    #if UMBREEY_SDK_WEBGL_WX_PERF
            WXPerfEngine.AddCustomStatInfoBy(name, value);
    #endif
        }
    }
}
