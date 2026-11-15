using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Umbreey;

[CreateAssetMenu(menuName ="Umbreey/打包/逐一级子目录打包")]
public class ABRuleEachSubDirs : ABRuleBase
{
    public override List<BuildData> Pack(ABPack pack)
    { 
        string defaultABName = $"{pack.PackName}_default";
        HashSet<string> checkBundleName = new HashSet<string>() { defaultABName };
        
        List<BuildData> builds = new List<BuildData>();
        for (int i = 0; i < pack.DirAssets.Count; i++)
        {
            var dirAsset = pack.DirAssets[i];
            if (dirAsset == null)
                continue;
                    
            var dir = AssetDatabase.GetAssetPath(dirAsset);
            if (!AssetDatabase.IsValidFolder(dir))
                continue;
            
            var subdirs = Directory.GetDirectories(dir, "*.*", SearchOption.TopDirectoryOnly);
            foreach (var subdir in subdirs)
            {
                List<string> list = new List<string>();
                var files = Directory.GetFiles(subdir, "*.*", SearchOption.AllDirectories);
                pack.CollectFilesTo(files, list);
                
                if (list.Count == 0)
                    continue;
                
                string abName = $"{pack.PackName}_{new DirectoryInfo(subdir).Name}";
                if (!checkBundleName.Add(abName))
                {
                    abName = $"{abName}_{i}";
                }
                
                var aliases = GetAliases(list);
                var build = new BuildData(
                    abName, 
                    pack.PackVariant,
                    list.ToArray(),
                    aliases.ToArray());
                
                builds.Add(build);
            }
        }

        List<string> list2 = new List<string>();
        pack.CollectObjectsTo(pack.SingleAssets, list2);
        
        if (list2.Count > 0)
        {
            var aliases = GetAliases(list2);
            var build = new BuildData(
                defaultABName, 
                pack.PackVariant,
                list2.ToArray(),
                aliases.ToArray());
                
            builds.Add(build);
        }
        
        return builds;
    }
}