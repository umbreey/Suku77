using System;
using UnityEngine;

namespace Umbreey
{
    [System.Serializable]
    public class DeviceInfo
    {
        public string gpu;
        public string gpuVendor;
        public string gpuVersion;
        
        // webgl override
        public string brand;
        public string model;
        public string system;
        public string platform;
        
        // only android
        public string manufacturer;
        public string osVersion; // android now

        public override string ToString()
        {
            return JsonUtility.ToJson(this, true);
        }
    }
    
    public abstract class PlatformBase
    {
    #region TODO
        public abstract string OnKeyOperatorType();
    #endregion

        internal abstract void Init(Action<UmbreeySDK.StatusCode> callback, params string[] param);

        // 该渠道是否在启动时由游戏自行弹出隐私协议框
        internal virtual bool IsStartPolicy() { return false; }

        // login
        internal abstract void Login(Action<UmbreeySDK.StatusCode, UmbreeySDK.LoginData> callback, params string[] param);
        internal abstract void Logout(Action<UmbreeySDK.StatusCode> callback, params string[] param);
        internal abstract bool SupportsLogout();
        
        // pay
        internal abstract void Pay(Action<UmbreeySDK.StatusCode> callback, params string[] param);
        internal abstract void Pay2(Action<UmbreeySDK.StatusCode> callback, params string[] param);
        
        // share
        internal abstract void Share(Action<UmbreeySDK.StatusCode> callback, UmbreeySDK.ShareParams param);
        
        // custom
        internal abstract string GetCustomData(params string[] param);
        internal abstract string SetCustomData(params string[] param);
        
        // game
        internal virtual void OnGamePause(Action<bool> callback)
        {
            UmbreeySDK.onApplicationPause -= callback;
            UmbreeySDK.onApplicationPause += callback;
        }

        internal virtual void OffGamePause(Action<bool> callback)
        {
            UmbreeySDK.onApplicationPause -= callback;
        }

        internal virtual void QuitGame()
        {
            Application.Quit();
        }
        
        // utils
        public virtual DeviceInfo GetDeviceInfo()
        {
            return new DeviceInfo() {
                brand = SystemInfo.deviceModel,
                model = SystemInfo.deviceModel,
                system = SystemInfo.operatingSystem,
                platform = Application.platform.ToString(),
                gpu = SystemInfo.graphicsDeviceName,
                gpuVendor = SystemInfo.graphicsDeviceVendor,
                gpuVersion = SystemInfo.graphicsDeviceVersion
            };
        }
        
        internal abstract long GetAvailableFreeSpace();
        internal virtual Rect GetScreenSafeArea() { return Screen.safeArea; }
        
        internal abstract void OpenUrl(string url);
        internal abstract void Vibrate();

        internal abstract void SaveImageToPhotoAlbum(Action<UmbreeySDK.StatusCode> callback, Texture2D texture, string filename, string album);
        
        internal virtual string SaveFile(string filename, byte[] data)
        {
	        var path = System.IO.Path.Combine(Application.persistentDataPath, filename);
            try
            {
                System.IO.File.WriteAllBytes(path, data);
                return path;
            }
            catch (Exception ex)
            {
                UmbreeySDK.LogError($"saving: {path}, errMsg: {ex.Message}");
            }
	        return null;
        }
        
        internal virtual void DeleteFile(string filename)
        {
            var path = System.IO.Path.Combine(Application.persistentDataPath, filename);
            try
            {
                System.IO.File.Delete(path);
            }
            catch (Exception ex)
            {
                UmbreeySDK.LogError($"deleting: {path}, errMsg: {ex.Message}");
            }
        }
        
        
        // factory method
        internal static PlatformBase Create()
        {
            PlatformBase platform = null;
            
    #if UNITY_EDITOR
            platform = new PlatformPC();
    #else
        #if UNITY_ANDROID
            platform = new PlatformAndroid();
        #elif UNITY_IOS
            platform = new PlatformIOS();
        #elif UNITY_WEBGL
            #if UMBREEY_SDK_WEBGL_WX
                platform = new PlatformWebGLWX();
            #elif UMBREEY_SDK_WEBGL_DY
                platform = new PlatformWebGLDY();
            #else
                platform = new PlatformWebGL();
            #endif
        #elif UNITY_STANDALONE_WIN
            platform = new PlatformPC();
        #else
            Debug.LogError("not supported platform");
        #endif
    #endif // UNITY_EDITOR

            return platform;
        }
    }
}

