using System;
using UnityEngine;
using Umbreey;

namespace Scripts
{
    /// <summary>
    /// 资源启动检查（对齐 client_template GameDownload 主流程，当前实现聚焦编辑器/本地 AB）。
    /// PrepareCheck → LoadBundleMap → GetRegionInfo → StartCheck(Assets.Init) → finish(HybridUtils.Init)
    /// </summary>
    public static class GameDownload
    {
        private static Action s_OnFinish;

        public static bool DebugChecked { get; private set; } = true;
        public static string DebugJson { get; private set; }

        public static void PrepareCheck(Action<string> onDebugJsonLoaded)
        {
            DebugChecked = true;
            DebugJson = null;
            onDebugJsonLoaded?.Invoke(DebugJson);
        }

        public static void LoadBundleMap()
        {
            // 正式包在此读取本地/缓存 bundlemap；Suku77 编辑器阶段由 Assets.Init + ABPack 完成索引。
            Log.I("[GameDownload] LoadBundleMap (skip local cache for now)");
        }

        public static void GetRegionInfo(Action finish)
        {
            // 正式包在此请求区服/CDN；当前直接放行。
            Log.I("[GameDownload] GetRegionInfo ok");
            finish?.Invoke();
        }

        public static void StartCheck(Action finish)
        {
            s_OnFinish = finish;
            DebugChecked = true;

            if (!Assets.UseAB)
            {
                Log.I("[GameDownload] StartCheck UseAB=false → Assets.Init (Editor AssetDatabase)");
                Assets.Init(OnAssetsInitDone);
                return;
            }

            var abSource = GetAssetBundleSource();
            Log.I($"[GameDownload] StartCheck abSource={abSource}");

            if (abSource == AssetBundleSource.StreamingAssets ||
                abSource == AssetBundleSource.Default)
            {
                Assets.Init(OnAssetsInitDone);
                return;
            }

            if (abSource == AssetBundleSource.RemoteCDN ||
                abSource == AssetBundleSource.StreamingAssetsPlusUpdate)
            {
                var cdnUrl = GetCdn(Main.Inst.CDNSource);
                if (string.IsNullOrEmpty(cdnUrl))
                {
                    Log.W("[GameDownload] CDN empty, fallback StreamingAssets Init");
                    Assets.Init(OnAssetsInitDone);
                    return;
                }

                Assets.Init(OnAssetsInitDone, $"{cdnUrl}/{Assets.KBundleMapFileName}");
                return;
            }

            Log.E($"[GameDownload] unsupported abSource: {abSource}");
            OnAssetsInitDone(false);
        }

        private static void OnAssetsInitDone(bool success)
        {
            if (success)
            {
                Log.I("[GameDownload] Assets.Init success");
                var cb = s_OnFinish;
                s_OnFinish = null;
                cb?.Invoke();
            }
            else
            {
                Log.E("[GameDownload] Assets.Init failed");
            }
        }

        private static AssetBundleSource GetAssetBundleSource()
        {
            var s = Main.Inst != null ? Main.Inst.ABSource : AssetBundleSource.Default;
            if (s == AssetBundleSource.Default)
            {
                if (App.IsEditor)
                    s = AssetBundleSource.StreamingAssets;
                else
                    s = App.IsWebGL ? AssetBundleSource.RemoteCDN : AssetBundleSource.StreamingAssetsPlusUpdate;
            }

            if (App.IsWebGL && s != AssetBundleSource.RemoteCDN)
            {
                Log.W($"WebGL only support RemoteCDN, got {s}, force RemoteCDN");
                s = AssetBundleSource.RemoteCDN;
            }

            return s;
        }

        public static string GetCdn(CDNSource source)
        {
            // 占位：正式项目接到区服/CDN 配置后再填。
            if (source == CDNSource.Local)
                return string.Empty;
            Log.W($"[GameDownload] CDN source {source} not configured yet");
            return string.Empty;
        }
    }
}
