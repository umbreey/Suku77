using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Umbreey;

[CreateAssetMenu(menuName ="Umbreey/打包/不打包的原始资源")]
public class ABRuleRawFiles : ABRuleBase
{
    public override List<BuildData> Pack(ABPack pack)
    {
        var files = pack.GetFiles();
        if (files.Count == 0)
            return new List<BuildData>();
        
        var aliases = GetAliases(files);
        var build = new BuildData(
            pack.PackName, 
            pack.PackVariant, 
            files.ToArray(), 
            aliases.ToArray());

        return new List<BuildData>() { build };
    }

    public static string GetFileMd5Name(string assetName)
    {
        return GetFileMd5Name(assetName, out _);
    }
    
    public static string GetFileMd5Name(string assetName, out string md5)
    {
        md5 = "";
        
        if (string.IsNullOrEmpty(assetName))
            return null;
        
        var id = Path.GetFileNameWithoutExtension(assetName);
        var ext = Path.GetExtension(assetName);
        md5 = FileUtils.GetMD5Hash(assetName);

        return $"{id}_{md5}{ext}";
    }
}