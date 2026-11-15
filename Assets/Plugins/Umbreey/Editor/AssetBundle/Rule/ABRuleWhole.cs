using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Umbreey;

[CreateAssetMenu(menuName ="Umbreey/打包/整包打包")]
public class ABRuleWhole : ABRuleBase
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
}