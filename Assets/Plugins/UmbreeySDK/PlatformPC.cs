using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Umbreey
{
    public class PlatformPC : PlatformBase
    {
        public override string OnKeyOperatorType()
        {
            return "Unknown_Operator";
        }

        internal override void Init(Action<UmbreeySDK.StatusCode> callback, params string[] param)
        {
            callback?.Invoke(UmbreeySDK.StatusCode.Success);
        }

        internal override void Login(Action<UmbreeySDK.StatusCode, UmbreeySDK.LoginData> callback, params string[] param)
        {
            callback?.Invoke(UmbreeySDK.StatusCode.Success, UmbreeySDK.LoginData.Default);
        }

        internal override void Logout(Action<UmbreeySDK.StatusCode> callback, params string[] param)
        {
            callback?.Invoke(UmbreeySDK.StatusCode.Success);
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
            return null;
        }

        internal override string SetCustomData(params string[] param)
        {
            return null;
        }

        internal override long GetAvailableFreeSpace()
        {
            return long.MaxValue;
        }

        internal override void OpenUrl(string url)
        {
            Application.OpenURL(url);
        }

        internal override void Vibrate()
        {
            
        }
        
        internal override void QuitGame()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        internal override void SaveImageToPhotoAlbum(Action<UmbreeySDK.StatusCode> callback, Texture2D texture, string filename, string album)
        {
            if (filename.EndsWith(".jpg"))
            {
                File.WriteAllBytes($"{Application.persistentDataPath}/{filename}", texture.EncodeToJPG());
            }
            else if(filename.EndsWith(".png"))
            {
                File.WriteAllBytes($"{Application.persistentDataPath}/{filename}", texture.EncodeToPNG());
            }
            else if(filename.EndsWith(".webp"))
            {
                File.WriteAllBytes($"{Application.persistentDataPath}/{filename}", texture.EncodeToWebP());
            }
            else
            {
                File.WriteAllBytes($"{Application.persistentDataPath}/{filename}", texture.EncodeToPNG());
            }
            callback?.Invoke(UmbreeySDK.StatusCode.Success);
        }

    }    
}

