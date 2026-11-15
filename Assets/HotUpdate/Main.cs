using System.Collections;
using Scripts;
using UnityEngine;
using Umbreey;

namespace HotUpdate
{
    /// <summary>
    /// 热更侧启动入口（对齐 client_template HotUpdate.Main）。
    /// 由 HybridUtils 反射调用 Start()。
    /// </summary>
    public static class Main
    {
        public static void Start()
        {
            Log.I("[HotUpdate] Start");
            Log.EnableLogLevel = Log.LogLevel.Info;
            Umbreey.Umbreey.Inst.StartCoroutine(StartAsync());
        }

        private static IEnumerator StartAsync()
        {
            float t0 = Time.time;
            while (!GameDownload.DebugChecked)
            {
                if (Time.time - t0 >= 3f)
                    break;
                yield return null;
            }

            FirstStart();
        }

        private static void FirstStart()
        {
            Log.I("[HotUpdate] FirstStart");
            UICanvasToggleDef.Register();
#if UNITY_EDITOR
            ConfigLoader.TryLoadFromEditorFile();
#endif
            ModManager.AddMods(ModDef.Global);
            Reload();
        }

        public static void Reload()
        {
            Log.I("[HotUpdate] Reload");
            ModManager.RemoveMods(ModDef.Game);
            ModManager.AddMods(ModDef.Game);
        }
    }
}
