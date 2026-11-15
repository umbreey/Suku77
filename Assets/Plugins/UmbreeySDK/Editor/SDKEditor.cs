using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;

namespace Umbreey
{
    [System.Flags]
    public enum SDKFlags : int
    {
        NONE = 0,
        THINKING_ANALYTICS = 1 << 1,
        GRAVITY_ENGINE = 1 << 2
    }

    public enum WebGLSubTarget : int
    {
        DEFAULT = 0,
        WECHAT = 1,
        DOUYIN = 2
    }
    
    public class SDKEditor : EditorWindow
    {
        public static class Define
        {
            public const string ThinkingAnalytics = "UMBREEY_SDK_THINKING_ANALYTICS";
            public const string GravityEngine = "UMBREEY_SDK_GRAVITY_ENGINE";
            public const string GravityWechat = "GRAVITY_WECHAT_GAME_MODE";
            public const string GravityDouyin = "GRAVITY_BYTEDANCE_GAME_MODE";
            public const string WebglWechat = "UMBREEY_SDK_WEBGL_WX";
            public const string WebglDouyin = "UMBREEY_SDK_WEBGL_DY";
        }
        
        [SerializeField] private WebGLSubTarget webglSubTarget = WebGLSubTarget.DEFAULT;
        [SerializeField] private SDKFlags sdkFlags = SDKFlags.NONE;
        
        // gui
        private SerializedObject _serializedObject;
        private SerializedProperty _webglSubTypeProp;
        private SerializedProperty _sdkFlagsProp;
        
        [MenuItem("Umbreey/SDK Editor")]
        static void Start()
        {
            GetWindow<SDKEditor>("Umbreey SDK Tool");
        }

        private void OnEnable()
        {
            _serializedObject = new SerializedObject(this);
            _webglSubTypeProp = _serializedObject.FindProperty("webglSubTarget");
            _sdkFlagsProp = _serializedObject.FindProperty("sdkFlags");
            
            Load(out var flags, out var subTarget);
            sdkFlags = flags;
            webglSubTarget = subTarget;
        }
        
        private void OnGUI()
        {
            _serializedObject.Update();
            EditorGUILayout.PropertyField(_webglSubTypeProp, new GUIContent("WebGL Platform:"));
            EditorGUILayout.PropertyField(_sdkFlagsProp, new GUIContent("SDKs:"));
            _serializedObject.ApplyModifiedProperties();

            if (GUILayout.Button("Apply"))
            {
                Save(sdkFlags, webglSubTarget);
            }
            
            EditorGUILayout.Space();
            EditorGUILayout.Separator();
        }

        public static void Load(out SDKFlags flags, out WebGLSubTarget webglSubtarget)
        {
            var defineSymbols = GetScriptingDefineSymbols(BuildTargetGroup.WebGL); // 莉･webgl蟷ｳ蜿ｰ荳ｺ蜃・
            if (defineSymbols.Contains(Define.WebglWechat))
                webglSubtarget = WebGLSubTarget.WECHAT;
            else if (defineSymbols.Contains(Define.WebglDouyin))
                webglSubtarget = WebGLSubTarget.DOUYIN;
            else
                webglSubtarget = WebGLSubTarget.DEFAULT;
            
            flags = SDKFlags.NONE;
            if (defineSymbols.Contains(Define.ThinkingAnalytics))
                flags |= SDKFlags.THINKING_ANALYTICS;
            if (defineSymbols.Contains(Define.GravityEngine))
                flags |= SDKFlags.GRAVITY_ENGINE;
        }

        public class SaveParam
        {
            public bool applyGravitySubmode = false;
            public Action callback = null;
            
            public Action GetCallbackForBuildGroup(BuildTargetGroup group)
            {
                return (group == BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget)) ? callback : null;
            }
        }
        
        public static void Save(SDKFlags flags, WebGLSubTarget webglSubtarget, SaveParam param = null)
        {
            param ??= new SaveParam();
            
            // common
            bool hasThinking = flags.HasFlag(SDKFlags.THINKING_ANALYTICS);
            bool hasGravity = flags.HasFlag(SDKFlags.GRAVITY_ENGINE);
            
            var patchDefineSymbols = new Dictionary<string, bool>();
            patchDefineSymbols.Add(Define.ThinkingAnalytics, hasThinking);
            patchDefineSymbols.Add(Define.GravityEngine, hasGravity);
            patchDefineSymbols.Add(Define.GravityWechat, false);
            patchDefineSymbols.Add(Define.GravityDouyin, false);
            patchDefineSymbols.Add(Define.WebglWechat, false);
            patchDefineSymbols.Add(Define.WebglDouyin, false);
            
            var buildTargets = new List<BuildTargetGroup>() {
                BuildTargetGroup.Standalone, BuildTargetGroup.Android, BuildTargetGroup.iOS
            };
            foreach (var targetGroup in buildTargets)
            {
                PatchScriptDefineSymbols(patchDefineSymbols, targetGroup, param.GetCallbackForBuildGroup(targetGroup));
            }
            
            // webgl specific
            if (webglSubtarget == WebGLSubTarget.WECHAT)
            {
                patchDefineSymbols[Define.WebglWechat] = true;
                if (hasGravity && param.applyGravitySubmode)
                {
                    patchDefineSymbols[Define.GravityWechat] = true;
                }
            }
            else if (webglSubtarget == WebGLSubTarget.DOUYIN)
            {
                patchDefineSymbols[Define.WebglDouyin] = true;
                if (hasGravity && param.applyGravitySubmode)
                {
                    patchDefineSymbols[Define.GravityDouyin] = true;
                }
            }
            PatchScriptDefineSymbols(patchDefineSymbols, BuildTargetGroup.WebGL, param.GetCallbackForBuildGroup(BuildTargetGroup.WebGL));
        }

        
    #region Utils
        public static HashSet<string> GetScriptingDefineSymbols(BuildTargetGroup group = BuildTargetGroup.Unknown)
        {
            if (group == BuildTargetGroup.Unknown)
            {
                group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            }
            
            var raw = PlayerSettings.GetScriptingDefineSymbolsForGroup(group) ?? string.Empty;
            return new HashSet<string>(raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
        }

        public static void SetScriptingDefineSymbols(HashSet<string> set, BuildTargetGroup group = BuildTargetGroup.Unknown, Action onCompiled = null)
        {
            if (group == BuildTargetGroup.Unknown)
            {
                group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            }

            var raw = string.Empty;
            if (set != null && set.Count > 0)
            {
                raw = string.Join(";", set);
            }

            Debug.Log($"SetScriptingDefineSymbolsForGroup {group}");
            PlayerSettings.SetScriptingDefineSymbolsForGroup(group, raw);
            
            // UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
            EditorApplication.ExecuteMenuItem("File/Save Project");
            
            if (onCompiled != null && !EditorApplication.isCompiling)
            {
                SDKEditorCallback.OffScriptCompiled();
                EditorApplication.delayCall += () => onCompiled?.Invoke();
            }
        }

        public static void PatchScriptDefineSymbols(Dictionary<string, bool> dict, BuildTargetGroup group = BuildTargetGroup.Unknown, Action onCompiled = null)
        {
            var set = GetScriptingDefineSymbols(group);
            foreach (var (symbol, isDefined) in dict)
            {
                if (isDefined)
                    set.Add(symbol);
                else
                    set.Remove(symbol);
            }
            
            SetScriptingDefineSymbols(set, group, onCompiled);
        }
    #endregion

    }

    public class SDKEditorCallback
    {
        private const string kOnScriptCompiled = "SDKEditor.OnScriptCompiled";
        
        public class CallbackInfo
        {
            public string className;
            public string methodName;
            public string paramStr;
        }
        
        [InitializeOnLoadMethod]
        private static void OnScriptCompiled()
        {
            var infoStr = EditorPrefs.GetString(kOnScriptCompiled, string.Empty);
            if (string.IsNullOrEmpty(infoStr))
            {
                return;
            }

            OffScriptCompiled();
            var info = JsonUtility.FromJson<CallbackInfo>(infoStr);
            Debug.Log($"OnScriptCompiled:{infoStr}");
            
            var type = Type.GetType($"{info.className ?? ""},Assembly-CSharp-Editor");
            if (type == null)
            {
                Debug.LogError($"class not found for reflection. info:{infoStr}");
                return;
            }
            
            var method = type.GetMethod(info.methodName ?? "", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (method == null)
            {
                Debug.LogError($"method not found in class. info:{infoStr}");
                return;
            }

            EditorApplication.delayCall += () =>
            {
                if (info.methodName == "BuildWebGL")
                {
                    var subtarget = Enum.Parse<WebGLSubTarget>(info.paramStr);
                    method.Invoke(null, new object[] { subtarget });
                }
                else
                {
                    method.Invoke(null, info.paramStr != null ? new object[] { info.paramStr } : null);
                }
            };
        }
        
        public static void OffScriptCompiled()
        {
            EditorPrefs.SetString(kOnScriptCompiled, string.Empty);
        }

        public static void OnScriptCompiled(CallbackInfo info)
        {
            var json = JsonUtility.ToJson(info);
            EditorPrefs.SetString(kOnScriptCompiled, json);
        }
    }
}
