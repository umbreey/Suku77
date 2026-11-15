using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Umbreey
{
    public partial class App 
    {
        public static int BuildNumber
        {
            get
            {
                // 从 Application.version 中解析 BuildNumber (格式: major.minor.buildnumber)
                string[] versionParts = Application.version.Split('.');
                if (versionParts.Length >= 3 && int.TryParse(versionParts[2], out int buildNumber))
                {
                    return buildNumber;
                }
                return 0; // 如果解析失败，返回0
            }
        }
        
        public static bool IsWebGL
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return true;
#endif
                return false;
            }
        }
        
        public static bool IsAndroid
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return true;
#endif
                return false;
            }
        }
        
        public static bool IsIOS
        {
            get
            {
#if UNITY_IOS && !UNITY_EDITOR
                return true;
#endif
                return false;
            }
        }
        
        public static bool IsWin
        {
            get
            {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
                return true;
#endif
                return false;
            }
        }
        
        public static bool IsEditor
        {
            get
            {
#if UNITY_EDITOR
                return true;
#endif
                return false;
            }
        }
        
        public static bool IsMobile => IsAndroid || IsIOS;
        public static string Version => Application.version.Replace(".", "_");
        public static string UserRoaming => System.Environment.GetEnvironmentVariable("AppData");

    }
}
