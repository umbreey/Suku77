using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Umbreey;

public abstract class ABRuleBase : ScriptableObject
{
    public class BuildData
    {
        public string bundleName { get; private set; } // 包名，默认小写
        public string bundleVariant { get; private set; } // 包变体，默认blk
        public string[] assetNames { get; private set; } // 文件路径
        public string[] aliasNames { get; private set; } // 文件名
        
        public BuildData(string bundleName, string bundleVariant, string[] assetNames, string[] aliasNames)
        {
            this.bundleName = bundleName.ToLower();
            this.bundleVariant = bundleVariant;
            this.assetNames = assetNames;
            this.aliasNames = aliasNames;
        }
    }
    
    public static readonly List<string> Variants = new List<string> { "blk" };
    public abstract List<BuildData> Pack(ABPack pack);
    
    protected string GetAlias(string file)
    {
        return Path.GetFileName(file);
    }
        
    protected List<string> GetAliases(List<string> files)
    {
        var aliases = files.ConvertAll(GetAlias);
        return aliases;
    }
}
