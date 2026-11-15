using System;
using UnityEngine;
using Umbreey;

namespace Scripts
{
    public enum AssetBundleSource : int
    {
        Unknown = 0,
        Default,
        StreamingAssets,
        StreamingAssetsPlusUpdate,
        RemoteCDN,
    }

    public enum CDNSource : int
    {
        Unknown = 0,
        Local,
        RemoteDebug,
        RemoteRelease,
    }

    public enum DispatchSource : int
    {
        Unknown = 0,
        InternalDev,
        ExternalTest,
        ExternalPub,
    }

    public enum NetworkType : int
    {
        Unknown = 0,
        WebSocket,
        Tcp,
    }

    [Serializable]
    public class RuntimeConfig
    {
        [LabelText("资源访问方式")]
        public AssetBundleSource ABSource = AssetBundleSource.Default;

        [LabelText("资源CDN")]
        public CDNSource CDNSource = CDNSource.Local;

        [LabelText("服务器地址")]
        public DispatchSource DispatchSource = DispatchSource.InternalDev;

        [LabelText("网络协议")]
        public NetworkType NetworkType = NetworkType.WebSocket;

        [LabelText("Debug服务器地址")]
        public string ServerName;

        [LabelText("Debug渠道")]
        public string DebugChannel;

        public void ApplyOverride(RuntimeConfig other)
        {
            if (other == null) return;
            if (other.ABSource != AssetBundleSource.Unknown) ABSource = other.ABSource;
            if (other.CDNSource != CDNSource.Unknown) CDNSource = other.CDNSource;
            if (other.DispatchSource != DispatchSource.Unknown) DispatchSource = other.DispatchSource;
            if (other.NetworkType != NetworkType.Unknown) NetworkType = other.NetworkType;
            if (!string.IsNullOrEmpty(other.ServerName)) ServerName = other.ServerName;
            if (!string.IsNullOrEmpty(other.DebugChannel)) DebugChannel = other.DebugChannel;
        }
    }

    [Serializable]
    public class EditorDebug
    {
        [LabelText("模拟热更加载")]
        public bool simulateHotUpdate;

        [LabelText("使用本地DLL")]
        public bool useLocalDll = true;

        [LabelText("加载本地符号")]
        public bool useLocalSymbol;
    }

    /// <summary>
    /// AOT 启动入口（对齐 client_template Scripts.Main）。
    /// Play 后：Log → SDK → GameDownload → HybridUtils → HotUpdate.Main。
    /// </summary>
    [RequireComponent(typeof(Umbreey.Umbreey))]
    public class Main : MonoBehaviour
    {
        [Header("运行时配置")]
        public RuntimeConfig runtimeConfig = new RuntimeConfig();

        [Header("编辑器调试")]
        public EditorDebug editorDebug = new EditorDebug();

        public static Main Inst { get; private set; }

        public AssetBundleSource ABSource => runtimeConfig.ABSource;
        public CDNSource CDNSource => runtimeConfig.CDNSource;
        public DispatchSource DispatchSource => runtimeConfig.DispatchSource;
        public NetworkType NetworkType => runtimeConfig.NetworkType;
        public string DebugServerName => runtimeConfig.ServerName;
        public string DebugChannel => runtimeConfig.DebugChannel;

        private ChannelSplash _channelSplash;
        public ChannelSplash ChannelSplash
        {
            get
            {
                if (_channelSplash == null && Umbreey.Umbreey.Inst != null && Umbreey.Umbreey.Inst.UIRoot)
                    _channelSplash = Umbreey.Umbreey.Inst.UIRoot.GetComponentInChildren<ChannelSplash>(true);
                return _channelSplash;
            }
        }

        void Awake()
        {
            Inst = this;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            QualitySettings.vSyncCount = 0;
        }

        void Start()
        {
            LogSystem.Init(LogCredentials.AppId, LogCredentials.AppSecret, LogCredentials.Topic);
            Log.I($"[Main] Start build:{App.BuildNumber} app:{Application.version} log:{LogSystem.LogPath}");

            UmbreeySDK.Init(code =>
            {
                Log.I($"[Main] SDK Init:{code}");
                LogSystem.SetChannel(string.IsNullOrEmpty(DebugChannel) ? "NONE" : DebugChannel);

                GameDownload.PrepareCheck(OnLocalDebugJsonLoaded);
                GameDownload.LoadBundleMap();
                GameDownload.GetRegionInfo(() =>
                {
                    GameDownload.StartCheck(HybridUtils.Init);
                });
            });
        }

        void OnApplicationQuit()
        {
            LogSystem.Flush();
        }

        void OnLocalDebugJsonLoaded(string debugJson)
        {
            if (string.IsNullOrEmpty(debugJson))
                return;

            try
            {
                var debugConfig = JsonUtility.FromJson<RuntimeConfig>(debugJson);
                runtimeConfig.ApplyOverride(debugConfig);
            }
            catch (Exception e)
            {
                Log.E($"[Main] Failed to parse debug data: {e.Message}");
            }
        }
    }
}
