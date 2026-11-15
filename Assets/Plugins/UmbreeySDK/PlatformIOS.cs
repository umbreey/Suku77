using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Umbreey
{
    public class PlatformIOS : PlatformBase
    {
        public override string OnKeyOperatorType() 
        {
            return string.Empty;
        }

        internal override void Init(Action<UmbreeySDK.StatusCode> callback, params string[] param)
        {
            callback?.Invoke(UmbreeySDK.StatusCode.Success);
        }

        internal override void Login(Action<UmbreeySDK.StatusCode, UmbreeySDK.LoginData> callback, params string[] param)
        {
            callback?.Invoke(UmbreeySDK.StatusCode.Success, new UmbreeySDK.LoginData()
            {
                Name = SystemInfo.deviceUniqueIdentifier
            });
        }

        internal override void Logout(Action<UmbreeySDK.StatusCode> callback, params string[] param)
        {
      
        }
        
        internal override bool SupportsLogout()
        {
            return true;
        }
        
        internal override void Pay(Action<UmbreeySDK.StatusCode> callback, params string[] param)
        {
        
        }

        internal override void Pay2(Action<UmbreeySDK.StatusCode> callback, params string[] param)
        {
        
        }

        internal override void Share(Action<UmbreeySDK.StatusCode> callback, UmbreeySDK.ShareParams param)
        {
           
        }

        internal override string GetCustomData(params string[] param)
        {
            return string.Empty;
        }

        internal override string SetCustomData(params string[] param)
        {
            return string.Empty;
        }

        internal override long GetAvailableFreeSpace()
        {
            return 0;
        }

        internal override void OpenUrl(string url)
        {
         
        }

        internal override void Vibrate()
        {
          
        }
        
        internal override void SaveImageToPhotoAlbum(Action<UmbreeySDK.StatusCode> callback, Texture2D texture, string filename, string album)
        {
            
        }
    }    
}

