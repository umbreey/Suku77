using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

public class PerfAnimation
{
    [MenuItem("Assets/Umbreey/Perf Stats Animation", true)]
    public static bool PrintStatsIsValidate ()
    { 
        var selected = Selection.activeObject;
        return selected is AnimationClip;
    }

    [MenuItem("Assets/Umbreey/Perf Stats Animation", false)]
    public static void PrintStats ()
    { 
        var clip = Selection.activeObject as AnimationClip;

        var path = AssetDatabase.GetAssetPath(clip);
        var fileInfo = new System.IO.FileInfo(path);
        var fileSize = fileInfo.Length;
        
        var memSize = Profiler.GetRuntimeMemorySize(clip);
        var blobSize = GetBlobSize(clip);
        
        Debug.Log($"[Perf] AnimationClip: {clip.name} \n" +
                  $"FileSize: {EditorUtility.FormatBytes((int)fileSize)}\n" +
                  $"MemorySize: {EditorUtility.FormatBytes(memSize)}\n" +
                  $"BlobSize: {EditorUtility.FormatBytes(blobSize)}\n");
    }

    // ref: https://blog.uwa4d.com/archives/Optimization_Animation.html
    // * blob size is much closer to real memory on phone than editor memory
    // * blob size is 0 if animation type is Legacy
    private static MethodInfo s_getAnimationClipStats;
    private static FieldInfo s_animationClipStatsSize;
    private static int GetBlobSize(AnimationClip clip)
    {
        if (s_getAnimationClipStats == null || s_animationClipStatsSize == null)
        {
            s_getAnimationClipStats = typeof(AnimationUtility).GetMethod("GetAnimationClipStats", BindingFlags.Static | BindingFlags.NonPublic);
            
            Assembly asm = Assembly.GetAssembly(typeof(Editor));
            Type aniclipstats = asm.GetType("UnityEditor.AnimationClipStats");
            s_animationClipStatsSize = aniclipstats.GetField ("size", BindingFlags.Public | BindingFlags.Instance);            
        }

        var stats = s_getAnimationClipStats.Invoke(null, new object[]{clip});
        var size = (int)s_animationClipStatsSize.GetValue(stats);

        return size;
    }
    
    public static void CompressClip(AnimationClip clip, bool keepScaleCurve = false)
    {
        var bindings = AnimationUtility.GetCurveBindings(clip);
        CompressCurves(clip, bindings, keepScaleCurve);

        bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
        CompressCurves(clip, bindings, keepScaleCurve);
    }
    
    private static void CompressCurves(AnimationClip clip, EditorCurveBinding[] bindings, bool keepScaleCurve)
    {
        foreach (EditorCurveBinding binding in bindings)
        {
            string name = binding.propertyName.ToLower();
            if (!keepScaleCurve)
            {
                if (name.Contains("scale"))
                {
                    AnimationUtility.SetEditorCurve(clip, binding, null);
                    continue;                    
                }
            }
	        
            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
            Keyframe[] keyframes = curve.keys;
            
            for (int i = 0; i < keyframes.Length; i++)
            {
                var key = keyframes[i];
                key.value = float.Parse(key.value.ToString("f3"));
                key.inTangent = float.Parse(key.inTangent.ToString("f3"));
                key.outTangent = float.Parse(key.outTangent.ToString("f3"));
                keyframes[i] = key;
            }
            
            curve.keys = keyframes;
            AnimationUtility.SetEditorCurve(clip, binding, curve);
        }
    }

}
