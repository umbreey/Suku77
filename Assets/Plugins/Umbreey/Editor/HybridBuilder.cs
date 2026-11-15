#if UMBREEY_HYBRIDCLR
using System.IO;
using HybridCLR.Editor;
using HybridCLR.Editor.AOT;
using HybridCLR.Editor.Installer;
using HybridCLR.Editor.Commands;
using UnityEngine;
using UnityEditor;
using Umbreey.Editor;


public class HybridBuilder
{
    private static readonly string HYBRIDCLR_DATA_PATH = $"{Application.dataPath}/../HybridCLRData";
    
    public static void BuildDLL(string outputDir)
    {
        if(!Directory.Exists(HYBRIDCLR_DATA_PATH))
        {
            InstallerController installerController = new InstallerController();
            installerController.InstallDefaultHybridCLR();
        }
        
        const string ext = "dll.bytes";
        PathUtils.ClearFiles(outputDir, new []{ $".{ext}", $".{ext}.meta"});
        
        var activeTarget = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
        if (PlayerSettings.GetScriptingBackend(activeTarget) == ScriptingImplementation.IL2CPP)
        {
            PrebuildCommand.GenerateAll();
            
            string aotDir = SettingsUtil.GetAssembliesPostIl2CppStripDir(EditorUserBuildSettings.activeBuildTarget);
            foreach (var name in SettingsUtil.AOTAssemblyNames)
            {
                EncryptCopy($"{aotDir}/{name}.dll",$"{outputDir}/{name}.{ext}", true);
            }  
        }
        else
        {
            CompileDllCommand.CompileDll(EditorUserBuildSettings.activeBuildTarget);
        }
        
        // copy
        string hotupdateDir = SettingsUtil.GetHotUpdateDllsOutputDirByTarget(EditorUserBuildSettings.activeBuildTarget);
        foreach (var name in SettingsUtil.HotUpdateAssemblyNamesExcludePreserved)
        {
            EncryptCopy($"{hotupdateDir}/{name}.dll",$"{outputDir}/{name}.{ext}");
        }
        
        AssetDatabase.Refresh();
    }

    static void EncryptCopy(string source, string dest, bool aotStripper = false)
    {
        if (File.Exists(dest))
            File.Delete(dest);
        
        Debug.Log($"copy {source} to {dest}");
        var bytes = File.ReadAllBytes(source);
        
        if (aotStripper)
        {
            bytes = AOTAssemblyMetadataStripper.Strip(bytes); 
        }
        bytes = HyBirdUtils.EncryptBytes(bytes);
        
        File.WriteAllBytes(dest, bytes);
    }
}
#endif
