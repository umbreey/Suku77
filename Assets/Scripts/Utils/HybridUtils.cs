using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Scripts;
using UnityEngine;
using Umbreey;

/// <summary>
/// 热更程序集加载与启动（对齐 client_template HyBirdUtils）。
/// 编辑器默认直接取 AppDomain 中的 HotUpdate；真机在定义 UMBREEY_HYBRIDCLR 时走 DLL 加载。
/// </summary>
public static class HybridUtils
{
    private const string HotUpdateDll = "HotUpdate";
    private static Assembly s_HotAssembly;

    public static void Init()
    {
        Log.I($"[Hybrid] Init UseAB={Assets.UseAB}");
        Umbreey.Umbreey.Inst.StartCoroutine(InternalInit());
    }

    private static IEnumerator InternalInit()
    {
        bool skipAB = false;
#if UNITY_EDITOR
        skipAB = Main.Inst != null && Main.Inst.editorDebug.useLocalDll;
#endif

        if (Assets.UseAB && !skipAB)
        {
#if UMBREEY_HYBRIDCLR
            // HybridCLR 真机路径：加载 HotUpdate.dll.bytes（需先用 HybridBuilder 产出）
            bool loaded = false;
            yield return Assets.LoadAssetEnumerator<TextAsset>($"{HotUpdateDll}.dll.bytes", asset =>
            {
                if (asset == null)
                {
                    Log.E($"[Hybrid] missing {HotUpdateDll}.dll.bytes");
                    return;
                }
                s_HotAssembly = Assembly.Load(asset.bytes);
                loaded = s_HotAssembly != null;
                if (loaded)
                    SetHotAssembly(s_HotAssembly);
            });
            if (!loaded)
                yield break;
#else
            Log.W("[Hybrid] UseAB=true but UMBREEY_HYBRIDCLR undefined; fallback AppDomain HotUpdate");
            s_HotAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == HotUpdateDll);
            SetHotAssembly(s_HotAssembly);
#endif
        }
        else
        {
            s_HotAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == HotUpdateDll);
            SetHotAssembly(s_HotAssembly);
        }

        if (s_HotAssembly == null)
        {
            Log.E("[Hybrid] HotUpdate assembly not loaded");
            yield break;
        }

        var splash = Main.Inst != null ? Main.Inst.ChannelSplash : null;
        if (splash != null && splash.isActiveAndEnabled)
        {
            Log.I("[Hybrid] Waiting for splash...");
            yield return splash.WaitFinish();
        }

        StartHotUpdateMain();
    }

    private static void StartHotUpdateMain()
    {
        if (s_HotAssembly == null)
            return;

        Type type = s_HotAssembly.GetType("HotUpdate.Main");
        if (type == null)
        {
            Log.E("[Hybrid] type HotUpdate.Main not found");
            return;
        }

        var method = type.GetMethod("Start", BindingFlags.Public | BindingFlags.Static);
        if (method == null)
        {
            Log.E("[Hybrid] HotUpdate.Main.Start not found");
            return;
        }

        Log.I("[Hybrid] Invoke HotUpdate.Main.Start");
        method.Invoke(null, null);
    }

    private static void SetHotAssembly(Assembly ass)
    {
        s_HotAssembly = ass;
        if (ass != null)
            Umbreey.Umbreey.SetReflectionAssembly(ass);
    }
}
