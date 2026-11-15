using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if UMBREEY_SDK_JPUSH
using JPush;

public partial class UmbreeySDK
{
    public static void JPushInit()
    {
        if (PlatformApp.IsMobile)
        {
            JPushBinding.SetDebug(true);
            JPushBinding.Init("UmbreeySDK");
            var id = JPushBinding.GetRegistrationId();
            
            if (!Permission.HasUserAuthorizedPermission("android.permission.POST_NOTIFICATIONS"))
            {
                Permission.RequestUserPermission("android.permission.POST_NOTIFICATIONS");
            }
        }
    }
}

#endif // UMBREEY_SDK_JPUSH
