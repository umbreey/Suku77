using System;
using System.Collections.Generic;
using UnityEngine;

namespace Umbreey
{
    [CreateAssetMenu(fileName = "LangAsset", menuName ="Umbreey/多语言/LangAsset")]
    public class LangAsset : ScriptableObject
    {
        public List<LangTableConfig> langTableConfigs = new List<LangTableConfig>();
        
        public LangTableConfig GetTableAssetConfig(SystemLanguage language)
        {
            var config = InternalGetTableAssetConfig(language);
            if (string.IsNullOrEmpty(config.tableName))
            {
                Log.E($"[LangAsset] GetTableAssetConfig: {language} not found.");
                return default;
            }
            return config;
        }
        
        public void SetTableAssetName(SystemLanguage language, string tableName, string fieldName)
        {
            for (int i = 0; i < langTableConfigs.Count; i++)
            {
                if (langTableConfigs[i].language == language)
                {
                    var langTable = langTableConfigs[i];
                    langTable.tableName = tableName;
                    langTable.fieldName = fieldName;
                    langTableConfigs[i] = langTable;
                    return;
                }
            }
            langTableConfigs.Add(new LangTableConfig()
            {
                language = language,
                tableName = tableName,
                fieldName = fieldName,
            });
        }
        
        private LangTableConfig InternalGetTableAssetConfig(SystemLanguage language)
        {
            foreach (var langTable in langTableConfigs)
            {
                if (langTable.language == language)
                {
                    return langTable;
                }
            }
            return default;
        }
    }

    [Serializable]
    public struct LangTableConfig
    {
        public SystemLanguage language;
        public string tableName;
        public string fieldName;
    }
}