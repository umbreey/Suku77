using System;
using System.Collections.Generic;
using UnityEngine;

namespace Umbreey
{
    [Serializable]
    public class LangLibraryData
    {
        public UILangLibrary library;
        public string key;
    }
    
    public class UILangLibrary : MonoBehaviour, IExportProcess
    {
        [Serializable]
        public class LangData
        {
            public string key;
            public string preview;
        }
        public string RootName;
        public List<LangData> langs = new List<LangData>();
        private Dictionary<string, string> _dictLang = new Dictionary<string, string>();
        private void Awake()
        {
            foreach (var lang in langs)
                _dictLang[lang.key] = lang.preview;
        }
        
        public void Process()
        {
#if UNITY_EDITOR
            langs.Clear();
#endif
        }

        public string GetText(string key, params object[] args)
        {
            string defaultValue;
            if (!_dictLang.TryGetValue(key, out defaultValue))
            {
                defaultValue = langs.Find(x => x.key == key)?.preview ?? key;
            }
            return Lang.GetText(key, defaultValue, args);
            //return Lang.GetText($"{GetFullKey(key)}",defaultValue,args);
        }
        
#if UNITY_EDITOR
        public string GetTextEditor(string key, params object[] args)
        {
            foreach (var lang in langs)
            {
                if (lang.key == key)
                    return string.Format(lang.preview,args);
            }
            return key;
        }
#endif
        
        public string GetFullKey(string name)
        {
            return $"{RootName}_{name}";
        }
    }
}
