using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Umbreey
{
    public class Lang
    {
        public const string LanguageArchiveKey = "Language";

        public static Dictionary<string, SystemLanguage> SupportedLanguages = new Dictionary<string, SystemLanguage>()
        {
            { "English", SystemLanguage.English },
            { "ChineseSimplified", SystemLanguage.ChineseSimplified },
            { "ChineseTraditional", SystemLanguage.ChineseTraditional },
        };
        
        public static SystemLanguage SystemLanguage =>  Application.systemLanguage;
        public static SystemLanguage CurrentLanguage => GetCurrentLanguage();
         
        private static Dictionary<string, string> s_Contents = new Dictionary<string, string>();

        public static SystemLanguage GetCurrentLanguage()
        {
            if (UmbreeyPlayerPrefs.HasKey(LanguageArchiveKey))
            {
                return (SystemLanguage)(UmbreeyPlayerPrefs.GetInt(LanguageArchiveKey));
            }
            return SystemLanguage;
        }
        
        public static void SetLang(SystemLanguage language)
        {
            UmbreeyPlayerPrefs.SetInt(LanguageArchiveKey, (int)language);
        }

        public static void SetLangContent(Dictionary<string, string> contents)
        {
            s_Contents.Clear();
            s_Contents = contents;
        }
        
        
        public static string GetText(string key)
        {
            return GetText(key,key);
        }
        
        public static string GetText(string key, string defaultValue)
        {
            string value;
            if (!s_Contents.TryGetValue(key, out value))
                value = defaultValue;
            return value;
        }
        
        public static string GetText(string key,params object[] args)
        {
            return GetText(key, key, args);
        }
        
        public static string GetText(string key,string defaultValue,params object[] args)
        {
            string value;
            if (!s_Contents.TryGetValue(key, out value))
                value = defaultValue;
            return string.Format(value, args);
        }
    }
}
