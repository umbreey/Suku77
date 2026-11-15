using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Umbreey;

[CreateAssetMenu(menuName ="Umbreey/打包/逐文件打包")]
public class ABRuleEachFiles : ABRuleBase
{
   public override List<BuildData> Pack(ABPack pack)
   {
      List<BuildData> builds = new List<BuildData>();
      
      var files = pack.GetFiles();
      foreach (var file in files)
      {
         string abName = Path.GetFileNameWithoutExtension(file).ToLower();
         string alias = GetAlias(file);
         
         var build = new BuildData(
            $"{pack.PackName}_{abName}", 
            pack.PackVariant, 
            new[] { file },
            new[] { alias });
         
         builds.Add(build);
      }
      
      return builds;
   }
}
