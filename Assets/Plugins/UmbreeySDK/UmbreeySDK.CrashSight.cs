using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if UMBREEY_SDK_CRASHSIGHT

public partial class UmbreeySDK
{
    public static void CrashSightInit(string appVersion)
    {
        if (PlatformApp.IsIOS)
        {
            CrashSightAgent.ConfigDebugMode (true);
            // 设置上报的目标域名，请根据项目需求进行填写。（必填）
            CrashSightAgent.ConfigCrashServerUrl("https://ios.crashsight.qq.com/pb/sync");
            // 设置上报所指向的APP ID, 并进行初始化。APPID可以在管理端更多->产品设置->产品信息中找到。
            CrashSightAgent.InitWithAppId("b117ec6d5b");
        }
        
        CrashSightAgent.ReRegistAllMonitors();
        CrashSightAgent.SetAppVersion(appVersion);
    }

    public static void SetCrashSightUserId(string userId)
    {
        CrashSightAgent.SetUserId(userId);
    }
    public static void SetCrashSightVersion(string version)
    {
        CrashSightAgent.SetAppVersion(version);
    }
}

#endif // UMBREEY_SDK_CRASHSIGHT
