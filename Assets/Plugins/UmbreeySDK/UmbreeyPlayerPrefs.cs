#if UMBREEY_SDK_WEBGL_DY && !UNITY_EDITOR
    #define UMBREEY_SDK_WEBGL_DY_RUNTIME
#endif 
using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace Umbreey
{
    /// <summary>
    /// 对于抖音小游戏，显式使用TTSDK空间下的PlayerPrefs，避免ttsdk.dll里覆写的全局PlayerPrefs因未使用而被裁剪掉
    /// 对于微信小游戏，因为插件runtime目录下的link.xml，指定wx-runtime.dll preserve="all"，确保覆写的全局PlayerPrefs不会被裁剪掉
    /// 对于其它平台，退化为UnityEngine.PlayerPrefs，因为没有更高优先级的全局空间PlayerPrefs
    /// </summary>
    public static class UmbreeyPlayerPrefs
    {
        public static void Save()
        {
    #if UMBREEY_SDK_WEBGL_DY_RUNTIME
            TTSDK.PlayerPrefs.Save();
    #else
            PlayerPrefs.Save();
    #endif
        }
        
        public static void SetInt(string key, int value)
        {
    #if UMBREEY_SDK_WEBGL_DY_RUNTIME
            TTSDK.PlayerPrefs.SetInt(key, value);
    #else
            PlayerPrefs.SetInt(key, value);
    #endif
        }

        public static int GetInt(string key, int defaultValue = 0)
        {
    #if UMBREEY_SDK_WEBGL_DY_RUNTIME
            return TTSDK.PlayerPrefs.GetInt(key, defaultValue);
    #else
            return PlayerPrefs.GetInt(key, defaultValue);
    #endif
            
        }

        public static void SetFloat(string key, float value)
        {
    #if UMBREEY_SDK_WEBGL_DY_RUNTIME
            TTSDK.PlayerPrefs.SetFloat(key, value);
    #else
            PlayerPrefs.SetFloat(key, value);
    #endif
        }

        public static float GetFloat(string key, float defaultValue = 0.0f)
        {
    #if UMBREEY_SDK_WEBGL_DY_RUNTIME
            return TTSDK.PlayerPrefs.GetFloat(key, defaultValue);
    #else
            return PlayerPrefs.GetFloat(key, defaultValue);
    #endif
        }

        public static void SetString(string key, string value)
        {
    #if UMBREEY_SDK_WEBGL_DY_RUNTIME
            TTSDK.PlayerPrefs.SetString(key, value);
    #else
            PlayerPrefs.SetString(key, value);
    #endif
        }

        public static string GetString(string key, string defaultValue = "")
        {
    #if UMBREEY_SDK_WEBGL_DY_RUNTIME
            return TTSDK.PlayerPrefs.GetString(key, defaultValue);
    #else
            return PlayerPrefs.GetString(key, defaultValue);
    #endif
        }

        public static void SetBool(string key, bool value)
        {
    #if UMBREEY_SDK_WEBGL_DY_RUNTIME
            TTSDK.PlayerPrefs.SetInt(key, value ? 1 : 0);
    #else
            PlayerPrefs.SetInt(key, value ? 1 : 0);
    #endif
        }

        public static bool GetBool(string key, bool defaultValue)
        {
    #if UMBREEY_SDK_WEBGL_DY_RUNTIME
            return TTSDK.PlayerPrefs.GetInt(key, defaultValue ? 1 : 0) == 1;
    #else
            return PlayerPrefs.GetInt(key, defaultValue ? 1 : 0) == 1;
    #endif
        }

        public static bool HasKey(string key)
        {
    #if UMBREEY_SDK_WEBGL_DY_RUNTIME
            return TTSDK.PlayerPrefs.HasKey(key);
    #else
            return PlayerPrefs.HasKey(key);
    #endif
        }
        
        public static void DeleteKey(string key)
        { 
    #if UMBREEY_SDK_WEBGL_DY_RUNTIME
            TTSDK.PlayerPrefs.DeleteKey(key);
    #else
            PlayerPrefs.DeleteKey(key);
    #endif
        }

        public static void DeleteAll()
        {
    #if UMBREEY_SDK_WEBGL_DY_RUNTIME
            TTSDK.PlayerPrefs.DeleteAll();
    #else
            PlayerPrefs.DeleteAll();
    #endif
        }
    }    
}
