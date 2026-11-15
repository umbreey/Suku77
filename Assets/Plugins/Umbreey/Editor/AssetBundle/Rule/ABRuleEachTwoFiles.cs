using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Umbreey;

[CreateAssetMenu(menuName ="Umbreey/打包/逐文件两级前缀打包")]
public class ABRuleEachTwoFiles : ABRuleBase
{
    public override List<BuildData> Pack(ABPack pack)
    {
        Dictionary<string,List<string>> dict = new Dictionary<string, List<string>>();
        
        var files = pack.GetFiles();
        foreach (var file in files)
        {
            var array = Path.GetFileNameWithoutExtension(file).ToLower().Split("_");
            var abName = array[0];
            
            if (array.Length > 1)
            {
                abName = $"{abName}_{array[1]}";
            }

            if (!dict.TryGetValue(abName, out var list))
            {
                dict[abName] = new List<string>();
                list = dict[abName];
            }
            
            list.Add(file);
        }
        
        List<BuildData> builds = new List<BuildData>();
        foreach (var (abName, list) in dict)
        {
            var aliases = GetAliases(list);
            var build = new BuildData(
                $"{pack.PackName}_{abName}", 
                pack.PackVariant,
                list.ToArray(),
                aliases.ToArray());
            
            builds.Add(build);
        }
        
        return builds;
    }
}