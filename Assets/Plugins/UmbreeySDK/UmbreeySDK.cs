using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Umbreey;

#if UNITY_EDITOR
    using UnityEditor;
    using UnityEditor.Build;
    using UnityEditor.Build.Reporting;
#endif // UNITY_EDITOR

public partial class UmbreeySDK
{
    internal class InternalMono : MonoBehaviour
    {
        void OnApplicationPause(bool pauseStatus)
        {
            onApplicationPause?.Invoke(pauseStatus);
        }
    }
    
    internal static event Action<bool> onApplicationPause;
    internal static GameObject RootObj { get; private set; }
	
    public static PlatformBase Platform { get; private set; }

    private static InternalMono _internalMono;
    static UmbreeySDK()
    {
        RootObj = new GameObject("UmbreeySDK");
        GameObject.DontDestroyOnLoad(RootObj);     

        _internalMono = RootObj.AddComponent<InternalMono>();
        
    #if UMBREEY_SDK_POCO
        RootObj.AddComponent<PocoManager>();
    #endif
        
        Platform = PlatformBase.Create();
    }

    public static void Init(Action<StatusCode> callback)
    {
        Platform.Init(callback);
    }

    internal static void StartCoroutine(IEnumerator routine)
    {
        _internalMono.StartCoroutine(routine);
    }
    
#region Log
    const string KLogTag = "[UmbreeySDK]";
    internal static void Log(string msg)
    {
        Debug.Log($"{KLogTag} {msg}");
    }

    internal static void LogWarning(string msg)
    {
        Debug.LogWarning($"{KLogTag} {msg}");
    }
    
    internal static void LogError(string msg)
    {
        Debug.LogError($"{KLogTag} {msg}");
    }
#endregion    


#region Common Types
    public enum StatusCode : int
    {
        Unknown = -1,
        Success = 0,
        Failure,
        Cancel,
        Fallback
    }
    
    public struct ShareParams
    {
        public string Title;
        public string Desc;
        public string Url; // image path etc.
        
        // options
        public string Template;
        public string Query;
    }

    public enum LoginFuncCode
    {
        None = 0,
        SWITCH_ACCOUNT,
        AGE_LIMIT
    }
    
    public struct LoginData
    {
        public string Token;
        public string Name; // or other meanings
        public LoginFuncCode FuncCode;
        
        public static LoginData Default => new LoginData
        {
            Name = SystemInfo.deviceUniqueIdentifier
        };
    }
#endregion


#region Platform Wrapper

    // 该渠道是否在启动时由游戏自行弹出隐私协议框，非安卓平台默认为 false
    public static bool IsStartPolicy()
    {
        return Platform.IsStartPolicy();
    }

    public static void Login(Action<StatusCode, LoginData> callback, params string[] param)
    {
        Platform.Login(callback, param);
    }
    
    public static void Logout(Action<StatusCode> callback, params string[] param)
    {
        Platform.Logout(callback, param);
    }
    
    public static bool SupportsLogout()
    {
        return Platform.SupportsLogout();
    }
    
    public static void Pay(Action<StatusCode> callback, params string[] param)
    {
        Platform.Pay(callback, param);
    }

    public static void Pay2(Action<StatusCode> callback, params string[] param)
    {
        Platform.Pay2(callback, param);
    }
    
    public static void Share(Action<StatusCode> callback, ShareParams param)
    {
        Platform.Share(callback, param);
    }

    public static string GetCustomData(params string[] param)
    {
        string val = Platform.GetCustomData(param);
        Log($"GetCustomData:{string.Join(",", param)} Value={val}");
        return val;
    }
    
    public static string SetCustomData(params string[] param)
    {
        Log($"SetCustomData:{string.Join(",", param)}");
        return Platform.SetCustomData(param);
    }
    
    public static long GetAvailableFreeSpace()
    {
        return Platform.GetAvailableFreeSpace();
    }
    
    public static Rect GetScreenSafeArea()
    {
        return Platform.GetScreenSafeArea();
    }
    
    public static void OpenUrl(string url)
    {
        Platform.OpenUrl(url);
    }

    public static void Vibrate()
    {
        Platform.Vibrate();
    }
    
    public static void OnGamePause(Action<bool> callback)
    {
        Platform.OnGamePause(callback);
    }
    
    public static void OffGamePause(Action<bool> callback)
    {
        Platform.OffGamePause(callback);
    }
    
    public static void QuitGame()
    {
        Platform.QuitGame();
    }

    public static void SaveImageToPhotoAlbum(Action<StatusCode> callback, Texture2D texture, string filename, string album = null)
    {
        if (string.IsNullOrEmpty(filename) || texture == null)
        {
            callback?.Invoke(StatusCode.Failure);
            return;
        }
        
        Platform.SaveImageToPhotoAlbum(callback, texture, filename, album);
    }
    
    public static string SaveFile(string filename, byte[] data)
    {
        if (string.IsNullOrEmpty(filename) || data == null)
            return null;
        
        return Platform.SaveFile(filename, data);
    }
    
    public static void DeleteFile(string filename)
    {
        if (string.IsNullOrEmpty(filename)) return;
        Platform.DeleteFile(filename);
    }
#endregion
}
