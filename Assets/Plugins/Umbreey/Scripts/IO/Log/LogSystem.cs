using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace Umbreey
{
    /// <summary>
    /// 日志系统的门面：采集、本地卷、打包、上传的总入口。
    /// 公开 API 是冻结契约 C5（specs/0000-index.md），宿主（S5）只依赖这一份。
    ///
    /// <para>
    /// 票 03 落地了**采集与本地卷**这条最小闭环：Attach 起线程与卷、
    /// logMessageReceivedThreaded 全量采集、双缓冲背压、打包线程每次唤醒立即写卷。
    /// 票 04 补上启动时的一次性卷保留清理（实现全在 <see cref="LogVolume"/> 里）。
    /// 票 05 在采集入口前面加了限流闸（<see cref="LogThrottle"/>，两层独立计数 + 抑制期心跳），
    /// 并给系统自产日志补上了同类事件限频。
    /// 票 06 补上生命周期：pause / quit / 域重载三个信号收敛到**一条**有界停止顺序
    /// （<see cref="LogStopSequence"/>），flush 用**序号水位**握手（<see cref="LogFlushGate"/>）。
    /// 至此本地日志部分完整。
    /// 尚未落地的部分仍是空实现，按票逐个填：
    /// 07 批次序列化 / 08 outbox / 09 上传循环 / 10 选址与时钟 / 11 宿主接入。
    /// </para>
    ///
    /// <para>
    /// 线程模型：公开 API **只在主线程调用**（Init 会取 Application.version 与
    /// UmbreeySDK.Platform.GetDeviceInfo()，后者在 Android 上走 JNI）。
    /// 跨线程传递一律经由不可变快照 <see cref="LogContextSnapshot"/>（契约 C2）——
    /// **禁止**让打包线程回调业务侧的 getter。
    /// 打包线程与上传线程**不得调用任何 Unity API**（全局硬约束 1）。
    /// 唯一被允许的例外是 <see cref="Debug"/>.Log*：全局硬约束 6 明确要求系统自产的诊断日志
    /// 走 Debug.Log 以保证 logcat 可见，而这些日志正是打包线程产生的。
    /// </para>
    ///
    /// <para>
    /// 时间：所有**时间间隔**的度量用单调时钟 <see cref="UptimeMs"/>（全局硬约束 2）。
    /// 墙上时钟只用于三处：logs[].ts、outbox 文件名里的 createUnixSec、卷保留的天数判定。
    /// </para>
    /// </summary>
    public static class LogSystem
    {
        /// <summary>
        /// 敏感日志的哨兵前缀（全局硬约束 7）。调用点直接写字面量（可读性更好），
        /// 日志系统内部与单元测试引用这个常量。
        ///
        /// <para>
        /// 📌 **调用点的义务：任何把 token / key / 验证码 / secret 写进日志的地方，
        /// 正文必须以 <c>[#sensitive]</c> 开头。** 前缀必须在**正文最前面**
        /// （Log.cs 拼的时间戳在它前面是可以的，那是采集端之外的事），
        /// 不能塞在正文中间、也不能等到拼接完再补 —— 采集端只在正文开头的一段窗口里找它。
        /// 漏写的后果不是报错，是这条凭据被照常上传到 CLS。
        /// </para>
        ///
        /// <para>
        /// ⚠️ 采集端识别它时**必须在前 <see cref="LogRecord.SensitiveScanChars"/> 个字符内
        /// IndexOf，不能用 StartsWith** —— Log.cs 会在正文前拼一个固定 20 字符的时间戳，
        /// 而第三方 SDK 的日志没有这个前缀，位置不固定。剥离用 Remove(idx, len)，不是 Substring。
        /// 实现在 <see cref="LogRecord.TryStripSensitiveTag"/>。
        /// </para>
        /// <para>
        /// 命中的记录**照常写本地卷**（正文含 token，是排查登录问题的必要材料），
        /// 但**不进批次累加器**，因此不进 outbox、不进 CLS。
        /// 分流的唯一策略点是 <see cref="AppendUploadable"/>。
        /// </para>
        /// </summary>
        public const string SensitiveTag = "[#sensitive]";

        /// <summary>
        /// 系统自产诊断日志的统一前缀（全局硬约束 6）。真机联调靠它在 logcat 里筛。
        /// </summary>
        private const string InternalPrefix = "[LogSystem] ";

        /// <summary>client_id 的持久化文件名。**不用 PlayerPrefs**：见下方 LoadOrCreateClientId。</summary>
        private const string ClientIdFileName = "log_client_id.txt";

        /// <summary>Unix 纪元。DateTime 运算是 BCL，任意线程安全（全局硬约束 1）。</summary>
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// 进程内的单调时钟起点。**所有时间间隔的度量都走它**（全局硬约束 2）：
        /// 退避间隔、攒批窗口、重签名年龄判定、抑制心跳、Init 告警。
        /// 墙上时钟会被玩家改系统时间和服务器时间同步跳变污染，一律不用于间隔度量。
        /// <para>
        /// 全限定 System.Diagnostics.Stopwatch 而不 using 整个命名空间：
        /// 那会让 Debug.Log 在 UnityEngine.Debug 与 System.Diagnostics.Debug 之间歧义。
        /// </para>
        /// </summary>
        private static readonly System.Diagnostics.Stopwatch s_Uptime = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>SetXxx 之间互斥。只在主线程上争用，代价可忽略。</summary>
        private static readonly object s_ContextLock = new object();

        /// <summary>阈值配置。Init 之前是参数总表的默认值，永不为 null。</summary>
        private static volatile LogConfig s_Config = new LogConfig();

        /// <summary>
        /// 当前上下文快照（契约 C2）。主线程构造新实例后 volatile 换引用，打包线程只读。
        /// 永不为 null —— 初始是 Version 为 0 的 <see cref="LogContextSnapshot.Empty"/>。
        /// </summary>
        private static volatile LogContextSnapshot s_Context = LogContextSnapshot.Empty;

        /// <summary>上传接收端（S3 实现）。真实实现缺席时是不做事的空实现，永不为 null。</summary>
        private static volatile ILogBatchSink s_Sink = NullLogBatchSink.Instance;

        /// <summary>上传器（S4 实现）。真实实现缺席时是不做事的空实现，永不为 null。</summary>
        private static volatile ILogUploader s_Uploader = NullLogUploader.Instance;

        // ── 会话级状态。除 s_Running / s_Initialized 外都在 Attach 里一次写定，之后只读。 ──

        private static string s_TraceId = "";
        private static string s_ClientId = "";
        private static string s_Platform = "";

        private static LogBuffer s_Buffer;
        private static LogVolume s_Volume;
        private static Thread s_PackThread;
        private static AutoResetEvent s_Wake;

        /// <summary>
        /// flush 的**序号水位**握手（票 06）。主线程记下目标水位，打包线程报告已处理到哪个号。
        /// 跨线程读写，故 volatile；停机后置 null，调用方一律先抓成局部变量再判空。
        /// </summary>
        private static volatile LogFlushGate s_Gate;

        /// <summary>
        /// 是否已经向 <see cref="UmbreeySDK"/> 注册过 pause 回调。
        /// <para>
        /// <see cref="Init"/> 可能被调用多次（宿主重试、Editor 关掉 Domain Reload），
        /// 注册与反注册必须配对。底层 <c>PlatformBase.OnGamePause</c> 自己也做了
        /// "先 -= 再 +=" 的去重，这个标志额外保证的是**停机时知道该不该去摘**——
        /// 没 Init 过就去碰 UmbreeySDK，会把它的静态构造（new GameObject + DontDestroyOnLoad）
        /// 拖到停机路径上触发。
        /// </para>
        /// </summary>
        private static bool s_PauseHooked;

        /// <summary>
        /// 限流状态机（票 05）。采集回调在**任意线程**上问它"这条放不放行"，它内部自己加锁。
        /// <para>
        /// 停机时**刻意不置 null**：采集回调已经用 <see cref="s_Buffer"/> 做过一次判空，
        /// 再多一个可能在两次读之间变成 null 的静态引用只会多一条竞态路径。
        /// 会话边界靠 <see cref="Attach"/> 换一个新实例来划分。
        /// </para>
        /// </summary>
        private static volatile LogThrottle s_Throttle;

        /// <summary>打包线程的运行开关。跨线程读写，必须 volatile。</summary>
        private static volatile bool s_Running;

        private static bool s_Attached;

        /// <summary>Init 是否已被调用。打包线程读它做超时告警，故 volatile。</summary>
        private static volatile bool s_Initialized;

        private static bool s_InitWarned;

        /// <summary>Attach 时刻的单调时间，用于 InitWarnSec 的超时判定。</summary>
        private static long s_AttachUptimeMs;

        /// <summary>
        /// 正在产生系统自产日志。**[ThreadStatic]**：logMessageReceivedThreaded 是同步回调，
        /// 回调跑在打日志的那个线程上，因此这个线程局部标志既能给记录打上
        /// <see cref="LogRecord.Internal"/>，又天然是一道重入守卫
        /// （"写盘失败 → 打日志 → 再次写盘失败"的无限递归，全局硬约束 6）。
        /// <para>
        /// 票 05 在它之上补了另外两件事：**bypass 限流计数**（在
        /// <see cref="LogThrottle.Admit"/> 里，凭记录上的 <see cref="LogRecord.Internal"/> 判定）
        /// 与**同类事件限频**（<see cref="AllowInternalEvent"/>）。三者各管一段，不要混：
        /// 重入守卫挡的是"同一次内部日志把自己引发第二遍"，
        /// 限频挡的是"同一类事件在时间上刷屏"，bypass 保证的是触顶后这些话仍然发得出去。
        /// </para>
        /// </summary>
        [ThreadStatic] private static bool s_EmittingInternal;

        /// <summary>保护下面两个限频数组。跨线程：写盘失败在打包线程，Init 失败在主线程。</summary>
        private static readonly object s_InternalEventLock = new object();

        /// <summary>每一类内部事件上次放行的**单调**时刻。</summary>
        private static readonly long[] s_InternalEventUptimeMs = new long[(int)LogInternalEvent.Count];

        /// <summary>该类内部事件是否已经放行过 —— 0 时刻是合法值，不能拿 0 当"没发生过"。</summary>
        private static readonly bool[] s_InternalEventSeen = new bool[(int)LogInternalEvent.Count];

        /// <summary>
        /// 已经发生、但因限频还没报出去的背压丢弃条数。
        /// <para>
        /// 限频把"每次交换报一行"改成了"窗口允许时报一行"，如果被压住的那几次就此丢掉，
        /// 报出来的数字会**小于真实丢弃量** —— 而这个数字是"这次运行到底丢没丢日志"的唯一线索，
        /// 报小了比不报更坏。所以压住的时候累加，下次一起报。
        /// </para>
        /// <para>只由打包线程读写（<see cref="ReportDropped"/> 的唯一调用点在 PackLoop 里），无需同步。</para>
        /// </summary>
        private static int s_PendingDropped;

        // ────────────────────────────────────────────────────────────────────────────────
        //  公开 API —— 冻结契约 C5。签名不得再变。
        // ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 初始化。**由宿主在 Main.Start 调用**。
        ///
        /// <para>
        /// ⚠️ 调用时机是硬约束：必须在 MonoBehaviour 的 Awake/Start **之后**，
        /// 不能放进 RuntimeInitializeOnLoadMethod —— 它内部会取
        /// UmbreeySDK.Platform.GetDeviceInfo()，而 UmbreeySDK 的静态构造会 new GameObject +
        /// DontDestroyOnLoad，提前触发会把 SDK 初始化拖到宿主预期之外的时刻。
        /// 同样的理由，取设备信息**必须在主线程**：Android 上 GetDeviceInfo 走 JNI，
        /// 后台线程调用会崩。
        /// </para>
        ///
        /// <para>
        /// appId / appSecret / topic 是**显式参数而不是 <see cref="LogConfig"/> 的字段**
        /// （全局硬约束 4）—— 避免 secret 混进一个可能被整体 dump 的配置对象。
        /// appSecret 不得进入任何日志、不得被 ToString() 整体打印（全局硬约束 8：
        /// 它比"敏感信息"更严格，是"哪儿都不写"）。
        /// </para>
        /// </summary>
        /// <param name="cfg">null 表示全用参数总表的默认值。</param>
        public static void Init(string appId, string appSecret, string topic, LogConfig cfg = null)
        {
            if (cfg != null)
            {
                s_Config = cfg;
            }

            AppId = appId ?? "";
            AppSecret = appSecret ?? "";
            Topic = topic ?? "";
            s_Initialized = true;

            // 以下两处取值**必须留在主线程**，取到之后只以快照形态跨线程传递（契约 C2）。
            string appVersion = "";
            string device = "";
            string gpu = "";
            string system = "";

            try
            {
                // Application.version 原值，不是 App.Version（后者把 '.' 换成了 '_'）。
                appVersion = Application.version;
            }
            catch (Exception e)
            {
                InternalError("app version unavailable: " + e.Message);
            }

            try
            {
                DeviceInfo info = UmbreeySDK.Platform != null ? UmbreeySDK.Platform.GetDeviceInfo() : null;
                if (info != null)
                {
                    device = info.model;
                    gpu = info.gpu;
                    system = info.system;
                }
            }
            catch (Exception e)
            {
                // 设备信息取不到不该让宿主起不来：日志照常采集，只是批头少几个维度。
                InternalError("device info unavailable: " + e.Message);
            }

            ReplaceContext(null, null, null, null, appVersion, device, gpu, system);

            HookPause();

            // 激活 ILogBatchSink（契约 C4 补记「接线归属」把这一行指派给票 08 —— 在此之前
            // outbox 没有消费者，提前接线只是把字节丢掉）。
            //
            // 为什么在 Init 而不是 Attach：app_id 是批头必填，Attach 那一刻还没有它。
            // Attach~Init 之间的记录并不会因此丢掉 —— 它们躺在打包线程的累加器里，
            // 只要 Init 发生在攒批窗口（BatchLingerMs）到期之前，就会一起进第一个批次。
            //
            // 目录扫描与索引建立不在这里做：LogOutbox 的构造不碰 IO，第一次落盘时才由
            // 打包线程扫一次（理由同票 03 把卷清理放进打包线程 —— 别把它变成启动卡顿）。
            Sink = new LogBatchSink(LogOutbox.Create(s_Config, LogDir, s_TraceId).Submit);

            // 激活 ILogUploader（契约 C4 补记「09 号票落地时确认的四条接缝」把这一行指派给
            // 票 10 —— 在此之前没有选址状态机、没有 LogClock，提前接线只会在没有地址的
            // 状态下空转；理由与 Sink 指派给票 08 相同）。
            //
            // ⚠️ 顺序：必须排在上面那行之后。LogUploader.Install() 会起上传线程，
            // 而它第一件事就是去读 LogOutbox.Instance（虽然读到 null 也只是睡一轮）。
            // 也必须排在 AppId / AppSecret 落定之后 —— 签名要用它们。
            //
            // 装配、赋值、起线程三件事都在 Install() 里面（它自己是幂等的），
            // 因为这里只有一行的授权额度。非 Android / iOS 上它连线程都不起。
            LogUploader.Install();
        }

        /// <summary>
        /// 注册 pause 回调（票 06）。
        ///
        /// <para>
        /// 走 <c>UmbreeySDK.OnGamePause</c> 而不是自己挂一个 MonoBehaviour 的
        /// <c>OnApplicationPause</c>：**与业务同一条通道**，平台差异（Android 的
        /// <c>PlatformAndroid</c>、小游戏的 <c>PlatformWebGLWX</c> / <c>PlatformWebGLDY</c>
        /// 各自的 OnHide/OnShow）已经被封装过一遍，自己再挂一份就要把那些差异重新踩一遍。
        /// </para>
        /// <para>
        /// ⚠️ 注册点必须是 <see cref="Init"/> 而不是 <see cref="Attach"/>：
        /// 碰 <c>UmbreeySDK</c> 的任何成员都会触发它的静态构造
        /// （new GameObject + DontDestroyOnLoad），在 BeforeSceneLoad 触发就把 SDK 的初始化
        /// 拖到了宿主预期之外的时刻（specs/0001「启动与生命周期」）。
        /// </para>
        /// </summary>
        private static void HookPause()
        {
            if (s_PauseHooked)
            {
                return;
            }

            try
            {
                // 底层自己做了"先 -= 再 +="的去重，重复 Init 不会挂出两份回调。
                UmbreeySDK.OnGamePause(OnGamePauseChanged);
                s_PauseHooked = true;
            }
            catch (Exception e)
            {
                // SDK 起不来不该让日志系统跟着倒 —— 只是这次没有 pause flush。
                InternalError("pause hook unavailable: " + e.Message);
            }
        }

        /// <summary>
        /// 切后台 / 切回前台。<c>true</c> = 进入后台（Unity 的 <c>OnApplicationPause</c> 原义，
        /// 小游戏平台的 OnHide 也被 <c>PlatformWebGL*</c> 归一成了 true）。
        ///
        /// <para>
        /// **Android 上这是最后一次机会**：切后台之后系统随时可能杀进程，而且**不再有任何回调**。
        /// 所以这里要做两件事，都在 <see cref="FlushInternal"/> 的一个预算里完成：
        /// 把缓冲写进本地卷，以及**强制产出一个 outbox 批次**（下次启动补传）。
        /// </para>
        /// <para>
        /// ⚠️ **绝不在这里发网络请求** —— 协议 §4.5 明确禁止，且是 ANR 的经典成因。
        /// 上传是上传线程自己的事，pause 只负责"把内存变成文件"。
        /// </para>
        /// <para>
        /// 切回前台什么也不做：日志系统一直在跑，没有需要恢复的东西。
        /// （网络恢复要重置重试预算，那是 <see cref="ILogUploader.OnNetworkResumed"/> 的事，归票 09。）
        /// </para>
        /// </summary>
        private static void OnGamePauseChanged(bool paused)
        {
            if (!paused)
            {
                return;
            }

            FlushInternal(-1, true);
        }

        /// <summary>
        /// 设置上报地址列表。地址来自 dispatch 的 region_info，**没有内置兜底域名**，
        /// 因此拿到响应之前客户端只攒不发。
        /// </summary>
        /// <param name="addrs">null 或空数组 = 停发，但**不影响打包**（日志继续进 outbox）。</param>
        public static void EnableUpload(string[] addrs)
        {
            // 直接转交给上传器。真实实现缺席时这里落到空实现上，什么也不会发生 ——
            // 这正是 S1 能独立编译、独立跑通本地卷的原因（契约 C4 末段）。
            s_Uploader.SetAddresses(addrs);
        }

        /// <summary>
        /// **原子地**一次更新 uid 与 env（契约 C2 要求）。
        /// 拆成两次 Set 会出现"新 uid + 旧 env"的中间状态，被那一瞬间采集的日志会带着错误的组合。
        /// <para>退出登录返回登录界面时传 ("", "")，并同样递增版本号。</para>
        /// </summary>
        public static void SetIdentity(string uid, string env)
        {
            // null 一律当空串：契约 C2 要求"未登录时为 ''，不是 null"，
            // 而下面的 ReplaceContext 把 null 解释成"这个字段不变"，两种含义不能混。
            ReplaceContext(uid ?? "", env ?? "", null, null, null, null, null, null);
        }

        /// <summary>资源版本。LoadBundleMap 之后才有值，热更完成后会变。</summary>
        public static void SetResVersion(string v)
        {
            ReplaceContext(null, null, v ?? "", null, null, null, null, null);
        }

        /// <summary>渠道。</summary>
        public static void SetChannel(string channel)
        {
            ReplaceContext(null, null, null, channel ?? "", null, null, null, null);
        }

        /// <summary>
        /// 当前会话的卷路径，飞书一键发送用（"发一个卷 == 发这次运行的完整日志"）。
        /// <para>
        /// 卷还没开出来（WebGL、或 Attach 失败）时是空串 —— 调用方本来就要应对这种情况。
        /// 句柄是常开的，但卷用 FileShare.ReadWrite 打开，一键发送可以并发读它。
        /// </para>
        /// </summary>
        public static string LogPath
        {
            get
            {
                LogVolume volume = s_Volume;
                return volume != null ? volume.FilePath : "";
            }
        }

        /// <summary>本次会话的追踪标识。</summary>
        public static string TraceId
        {
            get { return s_TraceId; }
        }

        /// <summary>
        /// 设备维度的持久化标识，卸载重装才变。未登录阶段没有 uid，但一定有 client_id，
        /// 因此它是登录失败类问题唯一的检索锚点。
        /// </summary>
        public static string ClientId
        {
            get { return s_ClientId; }
        }

        /// <summary>
        /// 把内存里的日志推给打包线程并等它确认完成。
        ///
        /// <para>
        /// 语义是"置 flush 标志 + 唤醒打包线程 + 等它确认"，
        /// **写盘始终只由打包线程执行**，主线程只等待 —— 否则两个线程会同时碰同一个 FileStream
        /// （ADR-0001）。超时就放弃。
        /// </para>
        /// <para>
        /// **只保证"调用那一刻已经在缓冲里的记录"**：等待的目标是调用瞬间的序号水位
        /// （<see cref="LogBuffer.EnqueuedSeq"/>），之后到达的记录拿到的是更大的号，
        /// 不在本次的保证范围内。这条边界是刻意的 —— 否则一个持续打日志的线程能让
        /// Flush 永远追不上。
        /// </para>
        /// <para>
        /// 超时不抛异常，只留一条 <see cref="LogRecord.Internal"/> 诊断
        /// （"flush 超时，N 条未落盘"）。降级路径是明确的：没写完的记录**留在缓冲里**，
        /// 进程若在此之后被杀就随内存一起消失 —— 这是可接受的，但必须说出来。
        /// </para>
        /// </summary>
        /// <param name="timeoutMs">
        /// -1 表示用 <see cref="LogConfig.FlushWaitMs"/>（参数总表 300ms）。
        /// **不是"无限等待"** —— 这是契约 C5 补记明确定过的（specs/0000-index.md）。
        /// </param>
        public static void Flush(int timeoutMs = -1)
        {
            // 公开入口不强制切批：宿主可能在任意时机调它（票 11 的飞书一键发送就会），
            // 每次都切一个批出去会在 outbox 里堆出一地碎批。
            // 强制切批只发生在 pause —— 那一刻我们是真的在跟"进程随时被杀"赛跑。
            FlushInternal(timeoutMs, false);
        }

        /// <summary>
        /// <see cref="Flush"/> 的实现。语义是「置 flush 标志 + 唤醒打包线程 + **等它确认完成**」。
        ///
        /// <para>
        /// ⚠️ **写盘始终只由打包线程执行，主线程只等待**（ADR-0001 末段）。
        /// 主线程自己动手写，就会和打包线程同时操作同一个 FileStream。
        /// </para>
        /// <para>
        /// 确认用的是**序号水位**，不是布尔信号（票 06 最容易做错的一条）：
        /// 记下目标水位 N，等打包线程报告"已处理到 ≥ N"。布尔事件在
        /// "上一轮超时、确认迟到"时会残留一个已置位的信号，下一轮 Flush 一进来就被它满足，
        /// 于是**明明还有记录在缓冲里，Flush 却报成功**。水位没有这个失效模式：
        /// 迟到的确认带着旧的号，满足不了更高的目标。
        /// </para>
        /// </summary>
        /// <param name="forceBatch">
        /// true = 顺带**强制产出一个 outbox 批次**（pause 用）。把内存里的日志固化到磁盘，
        /// 下次启动补传。⚠️ 这里只是"落盘"，**绝不发网络请求**——
        /// pause 回调里做网络请求是协议 §4.5 明令禁止的，也是 ANR 的经典成因。
        /// </param>
        private static void FlushInternal(int timeoutMs, bool forceBatch)
        {
            LogConfig cfg = s_Config;

            // C5 补记：-1 的含义是"用 FlushWaitMs"，不是"无限等待"。
            int waitMs = timeoutMs < 0 ? cfg.FlushWaitMs : timeoutMs;

            // 全部先抓成局部变量：停机流程会把这些静态引用清掉，中途变 null 就成了竞态。
            LogBuffer buffer = s_Buffer;
            LogFlushGate gate = s_Gate;
            AutoResetEvent wake = s_Wake;

            if (buffer == null || gate == null || wake == null)
            {
                // 没挂载（WebGL / Attach 失败）或已经停机：没有打包线程可等，直接回。
                return;
            }

            // **目标水位就在这一行确定**：调用那一刻已入缓冲的最大序号。
            long target = buffer.EnqueuedSeq;
            long forceTicket = forceBatch ? gate.RequestForceBatch() : 0L;

            try
            {
                wake.Set();
            }
            catch (ObjectDisposedException)
            {
                // 和停机撞上了。等一个已经没人服务的水位只会白白耗掉 FlushWaitMs。
                return;
            }

            ReportFlush(gate.Wait(target, forceTicket, waitMs));
        }

        // ────────────────────────────────────────────────────────────────────────────────
        //  内部接缝 —— 供 IO/Log 内部的其他类型使用，不对宿主开放。
        // ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 进程启动至今的毫秒数，**单调递增**，不受系统时钟改动影响（全局硬约束 2）。
        /// 只能用于**间隔**度量，它不是墙上时间。后台线程读取安全。
        /// </summary>
        internal static long UptimeMs
        {
            get { return s_Uptime.ElapsedMilliseconds; }
        }

        /// <summary>当前阈值配置。约定 Init 之后不再改动，故跨线程只读无需同步。</summary>
        internal static LogConfig Config
        {
            get { return s_Config; }
        }

        /// <summary>
        /// 当前上下文快照。采集端读 <see cref="LogContextSnapshot.Version"/> 记进
        /// <see cref="LogRecord.CtxVer"/>；打包线程读它拼批头。返回的实例不可变，永不为 null。
        /// </summary>
        internal static LogContextSnapshot Context
        {
            get { return s_Context; }
        }

        /// <summary>批头必填的应用标识。由 <see cref="Init"/> 存下，S3 拼批头时取。</summary>
        internal static string AppId { get; private set; }

        /// <summary>
        /// 签名用的应用密钥。**只允许交给 <see cref="LogSigner"/>**：
        /// 不进日志、不进 ToString、不进任何 dump（全局硬约束 4、8）。
        /// </summary>
        internal static string AppSecret { get; private set; }

        /// <summary>CLS 的 topic。由 <see cref="Init"/> 存下，S3 拼批头时取。</summary>
        internal static string Topic { get; private set; }

        /// <summary>
        /// 日志目录（主线程预取的 persistentDataPath）。卷、client_id 文件、
        /// 以及票 08 的 outbox 子目录都落在它下面。Attach 之前是空串。
        /// </summary>
        internal static string LogDir { get; private set; }

        /// <summary>
        /// 上传接收端。S3 在票 07 用真实实现替换掉空实现；置 null 会退回空实现。
        /// </summary>
        internal static ILogBatchSink Sink
        {
            get { return s_Sink; }
            set { s_Sink = value ?? NullLogBatchSink.Instance; }
        }

        /// <summary>
        /// 上传器。S4 在票 09 用真实实现替换掉空实现；置 null 会退回空实现。
        /// 启停由本类负责（契约 C4）。
        /// </summary>
        internal static ILogUploader Uploader
        {
            get { return s_Uploader; }
            set { s_Uploader = value ?? NullLogUploader.Instance; }
        }

        /// <summary>
        /// 系统自产的诊断日志（全局硬约束 6）。走 Debug.LogError 保证 logcat 可见，
        /// 采集回调会据 <see cref="s_EmittingInternal"/> 把它标成
        /// <see cref="LogRecord.Internal"/>，并借同一个标志挡住重入。
        /// </summary>
        internal static void InternalError(string message)
        {
            if (s_EmittingInternal)
            {
                return;
            }

            s_EmittingInternal = true;
            try
            {
                Debug.LogError(InternalPrefix + message);
            }
            catch (Exception)
            {
                // 连打日志都失败就只能放弃 —— 这里再抛会把打包线程掀翻。
            }
            finally
            {
                s_EmittingInternal = false;
            }
        }

        /// <summary>
        /// 同类内部事件的限频闸（specs/0001「系统自产日志」）：同一类事件在
        /// <see cref="LogConfig.SuppressNoticeSec"/> 窗口内只放行一条。
        ///
        /// <para>
        /// 窗口**复用 SuppressNoticeSec** 而不是新增一个参数：参数总表（specs/0000-index.md）
        /// 是唯一事实源，而它里面没有"内部事件限频窗口"这一项。SuppressNoticeSec 的语义
        /// 本就是"系统自产通告之间至少隔这么久"，两处用法一致；真要拆成两个旋钮，
        /// 得先改参数总表，那是契约变更、不能在票里顺手做。
        /// </para>
        /// <para>
        /// ⚠️ 第一次总是放行 —— <see cref="s_InternalEventSeen"/> 存在的意义就是这个：
        /// 单调时钟的 0 时刻是合法值，拿"上次时刻 == 0"当"没发生过"会让进程刚起来的
        /// 头 60 秒里第一条事件被莫名其妙地吃掉。
        /// </para>
        /// </summary>
        /// <returns>true = 这一条可以发出去，并且窗口从现在重新计时。</returns>
        private static bool AllowInternalEvent(LogInternalEvent kind)
        {
            if (kind == LogInternalEvent.Unclassified)
            {
                return true;
            }

            long now = UptimeMs;
            long window = s_Config.SuppressNoticeSec * 1000L;

            lock (s_InternalEventLock)
            {
                int i = (int)kind;
                if (s_InternalEventSeen[i] && now - s_InternalEventUptimeMs[i] < window)
                {
                    return false;
                }

                s_InternalEventSeen[i] = true;
                s_InternalEventUptimeMs[i] = now;
                return true;
            }
        }

        /// <summary>同 <see cref="InternalError"/>，但走 Debug.Log（不是失败，只是事实通告）。</summary>
        internal static void InternalInfo(string message)
        {
            if (s_EmittingInternal)
            {
                return;
            }

            s_EmittingInternal = true;
            try
            {
                Debug.Log(InternalPrefix + message);
            }
            catch (Exception)
            {
            }
            finally
            {
                s_EmittingInternal = false;
            }
        }

        /// <summary>
        /// 把一段记录里**可上传的**那些追加进批次累加器，返回追加的字节数。
        ///
        /// <para>
        /// ⚠️ **这是整个敏感机制唯一的策略点**（全局硬约束 7）。
        /// 敏感记录已经在 <see cref="LogVolume.Write"/> 里正常写进了本地卷（正文含凭据，
        /// 那是排查登录问题的必要材料），这里只是不让它继续往 outbox / CLS 走。
        /// 将来要改成"脱敏后上传"或"上传到受限 topic"，只动这一个方法，
        /// 837 个调用点一行都不用改。
        /// </para>
        /// <para>
        /// S1 **不做逐条分组**：切批（含 CtxVer 切换）是 S3 的职责（契约 C4）。
        /// </para>
        /// <para>纯函数，无 IO、无 Unity API，可在 EditMode 里直接测。</para>
        /// </summary>
        internal static int AppendUploadable(IReadOnlyList<LogRecord> source, List<LogRecord> batch)
        {
            if (source == null || batch == null)
            {
                return 0;
            }

            int bytes = 0;
            for (int i = 0; i < source.Count; i++)
            {
                LogRecord record = source[i];
                if (record.Sensitive)
                {
                    continue;
                }

                batch.Add(record);
                bytes += record.Size;
            }

            return bytes;
        }

        // ────────────────────────────────────────────────────────────────────────────────
        //  启动与停机
        // ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 进程启动最早期的挂载点，**业务不调用**（契约 C5 首行）。
        /// 场景加载之前就位，才能采集到启动崩溃前的日志。
        ///
        /// <para>
        /// ⚠️ **全程不碰 UmbreeySDK** —— 它的静态构造会 new GameObject + DontDestroyOnLoad，
        /// 在 BeforeSceneLoad 触发会把 SDK 初始化拖到宿主预期之外的时刻。
        /// 设备信息统一在 <see cref="Init"/> 里取。
        /// </para>
        /// <para>
        /// 这里访问的 Unity API 全部在主线程：persistentDataPath / productName /
        /// Application.platform 都是预取值，取到之后只以字段/快照形态交给后台线程
        /// （全局硬约束 1）。
        /// </para>
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Attach()
        {
            if (s_Attached)
            {
                return;
            }

            s_Attached = true;
            s_AttachUptimeMs = UptimeMs;

            // Editor 里关掉 Domain Reload 时静态状态会跨会话留存，这些标志必须按会话重置，
            // 否则第二次进播放模式的 Init 告警会拿着上一次的结论。
            s_Initialized = false;
            s_InitWarned = false;

            // 限流的两层计数是**单会话**语义（参数总表：单会话总量 / 单会话异常条数），
            // 所以每次 Attach 换一个新的状态机 —— 沿用上一次的实例会让第二次进播放模式
            // 一开始就带着上一轮的配额，甚至一进去就是抑制状态。
            s_Throttle = new LogThrottle(s_Config);
            ResetInternalEventThrottling();

            // WebGL / 小游戏：平台矩阵里这一行是"只留接口和空实现"（全局硬约束 3）。
            // 那边既没有多线程也没有可用的文件系统，整段挂载直接跳过 ——
            // 公开 API 仍然可调用，只是什么也不发生。
#if !UNITY_WEBGL || UNITY_EDITOR
            try
            {
                s_TraceId = Guid.NewGuid().ToString("D");
                s_Platform = ResolvePlatform();

                // iOS 与 Android 共用 persistentDataPath，零分支（全局硬约束 3）。
                string dir = Application.persistentDataPath;
                LogDir = dir;
                Directory.CreateDirectory(dir);

#if UNITY_IOS && !UNITY_EDITOR
                // iOS 的 persistentDataPath 指向 Documents/，默认进 iCloud/iTunes 备份，
                // 上百 MB 日志进备份可能影响审核。这是 Unity 自封装的跨平台 API，不是原生调用。
                UnityEngine.iOS.Device.SetNoBackupFlag(dir);
#endif

                s_ClientId = LoadOrCreateClientId(dir);
                s_Context = new LogContextSnapshot(
                    0, null, null, null, null, null, s_Platform, null, null, null, s_ClientId, s_TraceId);

                s_Buffer = new LogBuffer(s_Config);
                s_Volume = new LogVolume(s_Config, dir, Application.productName, s_TraceId);
                s_Volume.Open();

                // 每次 Attach 换一个新闸门：水位是**会话内**的序号，和缓冲的序号同源。
                // 沿用上一次的实例会带着上一场会话的水位，新会话的第一次 Flush 直接被误满足。
                s_Gate = new LogFlushGate();

                s_Wake = new AutoResetEvent(false);
                s_Running = true;
                s_PackThread = new Thread(PackLoop);
                s_PackThread.Name = "UmbreeyLogPack";
                // 后台线程：它绝不能拖住进程退出。停机走 Shutdown 的显式 Join。
                s_PackThread.IsBackground = true;
                s_PackThread.Start();

                // 采集必须在最后挂：前面任何一步失败都不该留下"有采集没有接收端"的半吊子状态。
                Application.logMessageReceivedThreaded += OnLogMessage;

                // quit 是静态 .NET 事件，Attach 里就能注册，不需要 GameObject。
                Application.quitting += OnApplicationQuitting;

#if UNITY_EDITOR
                // 域重载前必须关线程、关句柄，否则句柄泄漏会让下次进播放模式打不开卷。
                UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
#endif

                // 挂好钩子之后才报，这样这一行自己也会落进卷里，成为卷的第一行 ——
                // 卷里有 trace_id 和自己的路径，QA 把卷发过来时不必再问"这是哪次运行"。
                // 真机上它同时是 logcat 里"日志落在哪"的答案。
                InternalInfo("volume " + s_Volume.FilePath + " trace=" + s_TraceId + " client=" + s_ClientId);
            }
            catch (Exception e)
            {
                // 挂载失败不能把宿主拖下水：游戏照常起，只是这一次没有日志系统。
                Debug.LogError(InternalPrefix + "attach failed: " + e);
                Shutdown();
            }
#endif
        }

        /// <summary>
        /// 停机：**有界的停止顺序**（票 06）。
        /// 停止接受新日志 → flush → 停打包线程 → 停上传线程 → 关句柄。
        ///
        /// <para>
        /// quit（<c>Application.quitting</c>）与 Editor 域重载（<c>beforeAssemblyReload</c>）
        /// 走的是**同一条路径**，不留两套 —— 票 03 为域重载单独关句柄的那段已经并进来了。
        /// 重复调用不抛异常：<see cref="s_Attached"/> 是闩，且序列里每一步各自吞异常。
        /// </para>
        /// <para>
        /// 顺序的每一步都有理由，见 <see cref="ILogStopSteps"/>。整条路径是**有界**的：
        /// 最坏情况 ≈ 一次 flush 预算 + 一次 Join 预算，都是 <see cref="LogConfig.FlushWaitMs"/>。
        /// </para>
        /// </summary>
        private static void Shutdown()
        {
            if (!s_Attached)
            {
                return;
            }

            s_Attached = false;
            LogStopSequence.Run(new RuntimeStopSteps());
        }

        private static void OnApplicationQuitting()
        {
            Shutdown();
        }

#if UNITY_EDITOR
        private static void OnBeforeAssemblyReload()
        {
            Shutdown();
        }
#endif

        /// <summary>
        /// 把 <see cref="LogSystem"/> 的那堆静态运行期状态包装成
        /// <see cref="ILogStopSteps"/> 的五步。顺序本身住在
        /// <see cref="LogStopSequence"/> 里，那是一段可以在 EditMode 里拿假实现验证的纯逻辑；
        /// 这里只负责"每一步具体做什么"。
        ///
        /// <para>
        /// 构造时就把线程 / 卷 / 信号量抓成实例字段：后面的步骤会把静态引用清掉，
        /// 抓成局部的才不会在中途变 null。
        /// </para>
        /// </summary>
        private sealed class RuntimeStopSteps : ILogStopSteps
        {
            private readonly Thread m_Thread = s_PackThread;
            private readonly LogVolume m_Volume = s_Volume;
            private readonly AutoResetEvent m_Wake = s_Wake;
            private readonly LogFlushGate m_Gate = s_Gate;
            private readonly int m_JoinWaitMs = s_Config.FlushWaitMs;

            /// <summary>打包线程有没有干净地退出。它退干净了，卷就已经是它自己关的。</summary>
            private bool m_PackThreadEnded;

            /// <summary>
            /// 摘掉全部入口，**让缓冲不再进新记录**。
            /// <para>
            /// 放在第一步是为了给 flush 一个不会再涨的目标水位 —— 采集还开着的话，
            /// flush 追的是一个移动靶。
            /// </para>
            /// <para>
            /// 代价是这之后系统自产的诊断日志（走 Debug.Log）不会再被采集进卷。
            /// 这是有意接受的：停机路径上的诊断本来就主要是给 logcat / Console 看的。
            /// </para>
            /// </summary>
            public void StopAccepting()
            {
                Application.logMessageReceivedThreaded -= OnLogMessage;
                Application.quitting -= OnApplicationQuitting;
#if UNITY_EDITOR
                UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
#endif

                if (s_PauseHooked)
                {
                    s_PauseHooked = false;
                    try
                    {
                        UmbreeySDK.OffGamePause(OnGamePauseChanged);
                    }
                    catch (Exception)
                    {
                        // 退出路径上 SDK 可能已经拆了。摘不掉也无所谓，进程正在走。
                    }
                }
            }

            /// <summary>
            /// 把缓冲里的东西交给打包线程写完再往下走。**主线程只等待，不自己写盘**
            /// （ADR-0001）。有界：最多 <see cref="LogConfig.FlushWaitMs"/>。
            /// </summary>
            public void FlushToDisk()
            {
                FlushInternal(-1, false);
            }

            /// <summary>
            /// 停打包线程。它退出前会跑 <see cref="FinalDrain"/>：最后一次排空 + 关卷 + 报水位。
            /// <para>Join 也是有界的 —— 磁盘卡死时宁可自己兜底关句柄，也不能把退出拖住。</para>
            /// </summary>
            public void StopPackThread()
            {
                s_Running = false;

                if (m_Wake != null)
                {
                    try
                    {
                        m_Wake.Set();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }

                if (m_Thread == null)
                {
                    // 线程压根没起来（Attach 中途失败）。卷得由下面那步兜底关。
                    return;
                }

                try
                {
                    m_PackThreadEnded = m_Thread.Join(m_JoinWaitMs);
                }
                catch (Exception)
                {
                    m_PackThreadEnded = false;
                }
            }

            /// <summary>
            /// 停上传线程。
            /// <para>
            /// ⚠️ <see cref="ILogUploader.Stop"/> **必须立刻返回并取消在飞的请求**：
            /// 一个 <see cref="LogConfig.HttpTimeoutMs"/>（10s）的 HTTP 挂在那儿，
            /// 退出就会卡 10 秒。实现方（S4，票 09）要在 Stop 里 cancel，
            /// 不能"等它自己超时"。09 合入之前这里落在空实现上。
            /// </para>
            /// <para>
            /// 排在 flush 之后：outbox 批次先落盘，上传线程才没有"还没存就被停掉"的东西。
            /// 排在关句柄之前：上传线程可能还在读 outbox 的文件。
            /// </para>
            /// </summary>
            public void StopUploader()
            {
                s_Uploader.Stop();
            }

            /// <summary>
            /// 关句柄、清静态引用。**任何路径都必须走到这里**，句柄泄漏的表现是
            /// "下次进播放模式打不开卷"，那正是票 03/06 人工验收里的那一条。
            /// </summary>
            public void CloseHandles()
            {
                s_PackThread = null;
                s_Buffer = null;
                s_Volume = null;
                s_Gate = null;

                // 打包线程干净退出时卷已经是它关的（它是 FileStream 的唯一所有者）。
                // 只有它没起来或没退干净时才轮到主线程抢着关 —— 宁可抢，也不能泄漏。
                if (!m_PackThreadEnded && m_Volume != null)
                {
                    m_Volume.Close();
                }

                // 线程确实结束了才敢释放信号量：它还活着的话会 WaitOne 一个已释放的句柄。
                if ((m_Thread == null || m_PackThreadEnded) && m_Wake != null)
                {
                    s_Wake = null;
                    m_Wake.Close();
                }

                // 闸门最后关：还卡在 Flush 里的调用方会立刻被放走，不必等满超时。
                if (m_Gate != null)
                {
                    m_Gate.Close();
                }
            }
        }

        // ────────────────────────────────────────────────────────────────────────────────
        //  采集
        // ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 采集回调（术语见 CONTEXT.md「采集」）。
        ///
        /// <para>
        /// ⚠️ 由**任意线程**调用 —— logMessageReceivedThreaded 在打日志的那个线程上同步回调，
        /// 而 TCPClient 就在 ThreadPool 线程上打日志。旧 LogWrite 在这里往一个无锁的
        /// StringBuilder 上追加，那是一个已经存在、只是还没炸的数据竞争。
        /// </para>
        /// <para>捕获**全部 LogType**，不做等级过滤（取舍见 ADR-0002）。</para>
        /// <para>
        /// 采集入口上有**两道闸**，方向相反、各管一段（票 05）：
        /// <see cref="LogThrottle"/> 在**进缓冲之前**按配额挡住新的（丢最新），
        /// <see cref="LogBuffer"/> 在**进了缓冲之后**按内存上限丢最旧的（保住崩溃现场）。
        /// </para>
        /// </summary>
        private static void OnLogMessage(string logString, string stackTrace, LogType type)
        {
            LogBuffer buffer = s_Buffer;
            LogThrottle throttle = s_Throttle;
            if (buffer == null || throttle == null)
            {
                return;
            }

            // 时间戳在回调里取一次。这是它唯一能被称为"日志产生时刻"的位置，
            // 也是第三方 SDK 日志能拿到正确时间的唯一途径（ADR-0002）。
            // DateTime.UtcNow 是 BCL，后台线程安全；设备原始时间，不做时钟校正（全局硬约束 5）。
            long tsUnixMs = (long)(DateTime.UtcNow - UnixEpoch).TotalMilliseconds;

            LogRecord record = LogRecord.Capture(
                tsUnixMs, type, logString, stackTrace, s_Context.Version, s_EmittingInternal);

            // 限流。Internal 日志在 Admit 内部整条 bypass（全局硬约束 6）——
            // 触顶后标记行和心跳正是靠这一条才发得出去。
            // 时刻由这里注入：状态机自己不读时钟，那几条与 60 秒有关的行为才测得住。
            LogThrottleDecision decision = throttle.Admit(record.Type, record.Size, record.Internal, UptimeMs);

            if (decision.Allow)
            {
                buffer.Add(record);

                // 日志到达立即唤醒打包线程，延迟 ≈ 线程调度延迟（ADR-0001）。
                // ⚠️ 被抑制时**不唤醒**：异常刷屏时缓冲里根本没有新东西，
                // 每条都 Set() 一次只会让打包线程按刷屏的频率空转一遍。
                AutoResetEvent wake = s_Wake;
                if (wake != null)
                {
                    try
                    {
                        wake.Set();
                    }
                    catch (ObjectDisposedException)
                    {
                        // 停机与采集回调撞上了。日志已经进缓冲，只是没人再来取，无所谓。
                    }
                }
            }

            // 标记行 / 心跳放在最后发：它自己会走 Debug.Log → 同步回到本方法（同一个线程），
            // 那一趟带着 Internal = true，在 Admit 里 bypass、进缓冲、并自己唤醒打包线程。
            // 递归到此为止 —— 内部日志不会再触发新的标记或心跳。
            if (decision.Notice != LogThrottleNotice.None)
            {
                EmitThrottleNotice(decision);
            }
        }

        /// <summary>
        /// 把限流状态机的决定说成一句人能读懂的话（全局硬约束 6：走 Debug.Log* 保证 logcat 可见）。
        ///
        /// <para>
        /// 这一行**必须能看出是哪一层触顶**：异常触顶报成"总量触顶"，会把排查方向从
        /// "谁在每帧抛异常"直接带到"谁在写大日志"，而这两件事的现场完全不同。
        /// </para>
        /// <para>
        /// ⚠️ 措辞上不说"被抑制的记录还在本地卷里"：被抑制的记录**根本没进过缓冲**，
        /// 本地卷和 outbox 会同时失去它（ADR-0001）。
        /// </para>
        /// <para>
        /// 走 <see cref="LogInternalEvent.Unclassified"/>（不限频）：心跳的节奏已经由
        /// <see cref="LogThrottle"/> 按 SuppressNoticeSec 卡死，标记行则是状态跃迁驱动的、
        /// 一次会话至多几条，再套一层限频只会把跃迁标记吃掉。
        /// </para>
        /// </summary>
        private static void EmitThrottleNotice(LogThrottleDecision decision)
        {
            LogConfig cfg = s_Config;
            string state = DescribeSuppressState(decision.State);

            if (decision.Notice == LogThrottleNotice.Marker)
            {
                InternalError(
                    "throttle engaged: " + state
                    + "; caps are exception=" + cfg.ExceptionLimitCount + " records, session="
                    + cfg.SessionLimitBytes + " bytes; suppressed records reach neither the local"
                    + " volume nor the outbox; a heartbeat follows every " + cfg.SuppressNoticeSec + "s");
                return;
            }

            InternalInfo(
                "throttle still active: " + state
                + "; suppressed " + decision.SuppressedSinceNotice + " record(s) since the last notice, "
                + decision.SuppressedTotal + " total this session");
        }

        /// <summary>
        /// 抑制状态的文案。**三种状态必须彼此可分**，这是票 05 里点名不能写反的一条。
        /// </summary>
        private static string DescribeSuppressState(LogSuppressState state)
        {
            bool exception = (state & LogSuppressState.Exception) != 0;
            bool session = (state & LogSuppressState.Session) != 0;

            if (exception && session)
            {
                return "exception count cap and session byte cap both reached, every level is suppressed";
            }

            if (session)
            {
                return "session byte cap reached, every level is suppressed";
            }

            if (exception)
            {
                return "exception count cap reached, only Error/Assert/Exception are suppressed"
                       + " (Log/Warning still pass)";
            }

            return "not suppressed";
        }

        // ────────────────────────────────────────────────────────────────────────────────
        //  生命周期 flush 的诊断（票 06）
        // ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 把一次 flush 的结果报出去。<see cref="DescribeFlush"/> 说 null 就什么也不报。
        /// <para>
        /// 叠了内部事件限频：pause / quit 会连着来（切后台再切回来、再切后台），
        /// 磁盘一直有问题时不限频就会连成一片。
        /// </para>
        /// </summary>
        private static void ReportFlush(LogFlushResult result)
        {
            string message = DescribeFlush(result);
            if (message == null)
            {
                return;
            }

            // 分成两类限频：超时和写盘失败是两种完全不同的故障，
            // 共用一个窗口会让先来的那条把后来的另一类吃掉。
            LogInternalEvent kind = result.Completed
                ? LogInternalEvent.FlushWriteFailed
                : LogInternalEvent.FlushIncomplete;

            if (!AllowInternalEvent(kind))
            {
                return;
            }

            InternalError(message);
        }

        /// <summary>
        /// 一次 flush 结束后该说什么。**纯函数**，不碰时钟不碰 IO，EditMode 里直接摆输入断输出
        /// （与 <see cref="DescribeSuppressState"/> 同一个套路：状态机给数据，门面给文案）。
        ///
        /// <para>
        /// 三种要说话的情形，措辞必须彼此可分：
        /// </para>
        /// <para>
        /// · **超时** —— 说清"N 条未落盘"以及它们的去向（留在缓冲里，进程被杀就没了）。
        ///   票 06 点名要求的降级路径就是这一句。
        /// </para>
        /// <para>
        /// · **写盘失败** —— 这一轮照样算结束（否则 Flush 只能等到超时），但必须留痕，
        ///   不然"日志没写进去"会表现成"卷里静悄悄地少了一段"。
        /// </para>
        /// <para>
        /// · **停机中途** —— 打包线程已经不在了，等不到确认。这不是超时，别报成超时。
        /// </para>
        /// </summary>
        /// <returns>null = 正常完成且本轮没有写盘失败，无需打扰任何人。</returns>
        internal static string DescribeFlush(LogFlushResult result)
        {
            if (result.Completed)
            {
                if (!result.WriteFailed)
                {
                    return null;
                }

                return "flush finished but the volume reported a write failure this round; "
                       + "the round still counts as finished so Flush returns instead of "
                       + "waiting out its timeout";
            }

            string reason = result.Closed
                ? "flush could not finish: the log system was stopping"
                : "flush timed out after " + result.WaitedForMs + "ms";

            string tail = result.WriteFailed
                ? "; the volume also reported a write failure this round"
                : "";

            // Pending 是**序号差**，因此是"未落盘条数"的上界：其中可能混着被背压丢掉、
            // 本来就永远不会落盘的记录。宁可报得略高，也不能让人以为一条没丢。
            return reason + ", up to " + result.Pending
                   + " record(s) are still in the memory buffer and not on disk; "
                   + "they are lost if the process is killed before the next round" + tail;
        }

        // ────────────────────────────────────────────────────────────────────────────────
        //  打包线程
        // ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 打包线程主循环。
        ///
        /// <para>
        /// ⚠️ **等待超时不是固定值，而是"距离下一次攒批截止还剩多久"**（ADR-0001 末段）。
        /// 日志到达时由 <see cref="s_Wake"/> 立即唤醒；空闲时一直睡到攒批窗口到期
        /// （最长 BatchLingerMs，一次唤醒）。写成固定间隔轮询就错了：短间隔会让线程空转，
        /// 长间隔会把本地卷的延迟拖到那个间隔上，而本地卷不参与攒批正是 ADR-0001 的结论。
        /// </para>
        /// <para>
        /// 攒批窗口用 <see cref="UptimeMs"/>（Stopwatch）计时，不用 DateTime.Now ——
        /// 玩家改系统时间会让墙上时钟跳变，把窗口拉长或缩短（全局硬约束 2）。
        /// </para>
        /// <para>
        /// ⚠️ 本方法及其调用的一切**不得触碰 Unity API**（全局硬约束 1），
        /// 唯一例外是 Debug.Log*（全局硬约束 6 明文要求）。
        /// </para>
        /// </summary>
        private static void PackLoop()
        {
            // 缓冲、卷、信号量在本线程的整个生命周期里都抓成局部变量：
            // 停机时主线程会把对应的静态引用清掉，抓成局部才不会在中途变成 null。
            // 本线程是卷这个 FileStream 的唯一所有者，从开始写到关闭都在这里。
            LogConfig cfg = s_Config;
            LogBuffer buffer = s_Buffer;
            LogVolume volume = s_Volume;
            AutoResetEvent wake = s_Wake;
            LogFlushGate gate = s_Gate;

            List<LogRecord> batch = new List<LogRecord>(cfg.BatchMaxCount);
            int batchBytes = 0;
            long deadline = UptimeMs + cfg.BatchLingerMs;

            try
            {
                // 卷保留策略（票 04）。**只在启动时做这一次**，不在每次写卷时做 ——
                // 因此 LocalMaxBytes 是"历史卷预算"而不是运行期硬上限，
                // 磁盘峰值 = LocalMaxBytes + VolumeMaxBytes（specs/0001「本地卷」）。
                //
                // 放在这里而不是 Attach 里：玩家设备上积累的旧格式日志可能有几百个，
                // 扫目录 + 批量删除挤进 BeforeSceneLoad 就是一次启动卡顿。
                // 它只碰 Directory / File（BCL，不是 Unity API，全局硬约束 1），
                // 也不碰卷的 FileStream，放在后台线程是安全的。
                // 内部全程吞异常，删不掉不影响下面的循环。
                if (volume != null)
                {
                    volume.CleanupOnStartup();
                }

                while (s_Running)
                {
                    long remain = deadline - UptimeMs;
                    if (remain < 0)
                    {
                        remain = 0;
                    }
                    else if (remain > cfg.BatchLingerMs)
                    {
                        // 系统时钟怎么跳都不影响这里，但配置被换掉时把窗口夹回合法区间。
                        remain = cfg.BatchLingerMs;
                    }

                    wake.WaitOne((int)remain);

                    WarnIfInitMissing(cfg);

                    // ⚠️ 取票必须在 Swap **之前**：先取票、后交换，这一轮才必然覆盖
                    // 请求发生那一刻缓冲里的全部记录。反过来（先换后取）会漏掉
                    // "换出之后、取票之前"这一小段时间里到达的记录，
                    // 而那正是 pause 那一瞬间最后几条日志所在的位置。
                    long forceTicket = gate != null ? gate.TakeForceBatch() : 0L;

                    int dropped;
                    long seq;
                    IReadOnlyList<LogRecord> records = buffer.Swap(out dropped, out seq);

                    bool writeOk = true;
                    if (records.Count > 0)
                    {
                        // 本地卷**立即写**，不参与攒批（ADR-0001）。
                        // 失败不外抛，但要如实带回来：写盘失败也算这一轮结束（票 06）。
                        writeOk = volume.Write(records);

                        // 再追加进批次累加器 —— 敏感记录就在这一步被拦下。
                        batchBytes += AppendUploadable(records, batch);
                    }

                    long now = UptimeMs;
                    bool linger = now >= deadline;

                    // 强制切批（pause）把攒批窗口整个短路掉：宁可出一个半满的批，
                    // 也不能让这些记录只躺在内存里等下一次 linger 到期 —— 切后台之后
                    // 系统随时会杀进程，而且**不再有任何回调**。
                    if (batch.Count > 0
                        && (forceTicket > 0 || linger || batch.Count >= cfg.BatchMaxCount
                            || batchBytes >= cfg.BatchMaxBytes))
                    {
                        // 整段交出去，S1 不做逐条分组（契约 C4）。
                        s_Sink.Write(batch);
                        batch = new List<LogRecord>(cfg.BatchMaxCount);
                        batchBytes = 0;
                        deadline = now + cfg.BatchLingerMs;
                    }
                    else if (batch.Count == 0)
                    {
                        // 累加器空着就把窗口整体后移，下一轮直接睡满一个 BatchLingerMs。
                        deadline = now + cfg.BatchLingerMs;
                    }

                    if (forceTicket > 0)
                    {
                        PersistSink();
                    }

                    if (dropped > 0)
                    {
                        ReportDropped(dropped);
                    }

                    // 报水位放在**最后**：Flush 拿到确认时，写卷、切批、outbox 落盘都已经做完。
                    // 提前报就等于对主线程撒谎 —— 它会在 outbox 批次还没写出去的时候就返回。
                    if (gate != null)
                    {
                        gate.ReportRound(seq, writeOk, forceTicket);
                    }
                }
            }
            catch (Exception e)
            {
                InternalError("pack thread aborted: " + e);
            }
            finally
            {
                FinalDrain(buffer, volume, batch, gate);
            }
        }

        /// <summary>
        /// **强制把攒批中的内容固化到磁盘**的接缝（票 06，pause 用）。
        ///
        /// <para>
        /// 为什么需要它：S1 把整段交给 <see cref="ILogBatchSink.Write"/> 之后，按契约 C4
        /// 切批是 S3 的职责，S3 手里很可能还留着一个没切完的尾巴。
        /// 只在 S1 这边切一刀并不能保证那部分已经到了磁盘上，而 pause 的全部意义就是
        /// "内存里的东西现在必须变成文件"。
        /// </para>
        /// <para>
        /// ⚠️ **只落盘，不发网络**（协议 §4.5 / ANR）。
        /// </para>
        /// <para>
        /// 08 号票尚未合入，这里落在空实现上（<see cref="NullLogBatchSink"/>），什么也不发生；
        /// 08 合入后 S3 的实现类补上 <see cref="ILogBatchSinkPersist"/> 就直接接上了。
        /// </para>
        /// <para>⚠️ 只由打包线程调用（写盘只能在这一个线程上发生）。</para>
        /// </summary>
        private static void PersistSink()
        {
            ILogBatchSinkPersist persist = s_Sink as ILogBatchSinkPersist;
            if (persist == null)
            {
                return;
            }

            try
            {
                persist.Persist();
            }
            catch (Exception e)
            {
                // outbox 落盘失败不能掀翻打包线程 —— 本地卷还得继续写。
                InternalError("forced outbox batch failed: " + e.Message);
            }
        }

        /// <summary>
        /// 退出循环前的最后一次排空：交换 → 写卷 → 交出批次 → **关句柄**。
        /// 全部在打包线程上做，因为它是 FileStream 的唯一所有者（ADR-0001）。
        /// <para>
        /// 关句柄放在 finally 里：上面任何一步失败都不能让卷带着句柄留在那儿，
        /// 否则下次进播放模式会打不开卷。
        /// </para>
        /// <para>
        /// ⚠️ **最后要把水位报到位并关掉闸门**（票 06）：主线程可能正卡在
        /// <see cref="FlushInternal"/> 里等确认。打包线程一声不吭地退出，那边就只能等满
        /// <see cref="LogConfig.FlushWaitMs"/>——退出流程白白多花 300ms，而且报一条假的超时诊断。
        /// </para>
        /// </summary>
        private static void FinalDrain(LogBuffer buffer, LogVolume volume, List<LogRecord> batch, LogFlushGate gate)
        {
            long seq = 0L;
            bool writeOk = true;

            try
            {
                if (buffer != null)
                {
                    int dropped;
                    IReadOnlyList<LogRecord> records = buffer.Swap(out dropped, out seq);
                    if (records.Count > 0 && volume != null)
                    {
                        writeOk = volume.Write(records);
                        AppendUploadable(records, batch);
                    }
                }

                if (batch.Count > 0)
                {
                    s_Sink.Write(batch);
                }

                // 退出也要把攒批的尾巴固化掉 —— 理由和 pause 一样，只是这次进程是自己走的。
                PersistSink();
            }
            catch (Exception)
            {
                // 停机路径上没人再看得到诊断日志，也没有补救手段。关句柄才是要紧事。
            }
            finally
            {
                if (volume != null)
                {
                    volume.Close();
                }

                if (gate != null)
                {
                    // 先把最后这一轮的水位报掉（这一批确实写出去了），再关闸门放走还在等的人。
                    // 顺序反了的话，那次 Flush 会拿到一个"没完成"的结果并报一条误导的诊断。
                    gate.ReportRound(seq, writeOk, LogFlushGate.ServeAllForceTickets);
                    gate.Close();
                }
            }
        }

        /// <summary>
        /// 背压丢弃的计数在**下次交换**时报一行（specs/0001「缓冲与打包线程」）。
        ///
        /// <para>
        /// 叠加了内部事件限频（票 05）：持续超限时打包线程每被唤醒一次就会拿到一个非零的
        /// 丢弃数，不限频的话一场异常刷屏能刷出每秒几百行 —— 而这些行自己是 Internal 日志、
        /// bypass 限流，谁也拦不住它们。
        /// </para>
        /// <para>
        /// ⚠️ **被限频压住的那几次要累加，不能丢**（<see cref="s_PendingDropped"/>）：
        /// 报出来的数字是"这次运行到底丢没丢日志"的唯一线索，报小了比不报更坏。
        /// 因此这一行的语义是"自上次报告以来累计丢弃 N 条"。
        /// </para>
        /// <para>
        /// ⚠️ 措辞上不要说"被丢的旧记录仍然在本地卷里"：在缓冲里被丢的记录**还没写过本地卷**，
        /// 两个接收端会同时失去它（ADR-0001）。
        /// </para>
        /// </summary>
        private static void ReportDropped(int dropped)
        {
            s_PendingDropped += dropped;

            // 限频闸在这里问、而不是交给 InternalError：只有先拿到"这次能发"，
            // 才知道该把累计数清成 0。
            if (!AllowInternalEvent(LogInternalEvent.BufferDropped))
            {
                return;
            }

            int total = s_PendingDropped;
            s_PendingDropped = 0;

            InternalError("dropped " + total + " records (buffer over BufferLimitBytes, oldest first)");
        }

        /// <summary>
        /// 把内部事件的限频状态清回"什么都没发生过"。会话边界（<see cref="Attach"/>）调用 ——
        /// Editor 关掉 Domain Reload 时静态数组会跨会话留存，不清的话新会话的第一条
        /// 丢弃报告可能因为上一次会话的时刻而被吃掉。
        /// </summary>
        private static void ResetInternalEventThrottling()
        {
            lock (s_InternalEventLock)
            {
                for (int i = 0; i < s_InternalEventSeen.Length; i++)
                {
                    s_InternalEventSeen[i] = false;
                    s_InternalEventUptimeMs[i] = 0L;
                }
            }

            s_PendingDropped = 0;
        }

        /// <summary>
        /// Attach 之后 InitWarnSec 仍未 Init 就喊一次 ——
        /// "业务忘了调 Init"的表现是"CLS 里没日志"，不喊出来非常难查。
        /// <para>由打包线程顺带检查：它至少每 BatchLingerMs 醒一次，粒度足够。</para>
        /// </summary>
        private static void WarnIfInitMissing(LogConfig cfg)
        {
            if (s_Initialized || s_InitWarned)
            {
                return;
            }

            if (UptimeMs - s_AttachUptimeMs < cfg.InitWarnSec * 1000L)
            {
                return;
            }

            s_InitWarned = true;
            InternalError(
                "Init was not called within " + cfg.InitWarnSec + "s after attach; "
                + "local volume is running but batches have no app_id and will not be uploaded");
        }

        // ────────────────────────────────────────────────────────────────────────────────
        //  上下文与身份
        // ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 构造一个新的上下文快照、递增版本号、volatile 换引用（契约 C2）。
        /// <para>
        /// 参数为 <c>null</c> 表示"这个字段保持不变"，空串表示"把它设成空"——
        /// 两者含义不同，公开 API 负责在调用之前把 null 归一成空串。
        /// </para>
        /// <para>
        /// Platform / ClientId / TraceId 是会话常量，不在参数里，永远从字段取。
        /// </para>
        /// </summary>
        private static void ReplaceContext(
            string uid, string env, string resVersion, string channel,
            string appVersion, string device, string gpu, string system)
        {
            lock (s_ContextLock)
            {
                LogContextSnapshot current = s_Context;
                s_Context = new LogContextSnapshot(
                    current.Version + 1,
                    uid ?? current.Uid,
                    env ?? current.Env,
                    resVersion ?? current.ResVersion,
                    channel ?? current.Channel,
                    appVersion ?? current.AppVersion,
                    s_Platform,
                    device ?? current.Device,
                    gpu ?? current.Gpu,
                    system ?? current.System,
                    s_ClientId,
                    s_TraceId);
            }
        }

        /// <summary>
        /// android / ios / windows / mac / web（契约 C2）。**必须在主线程调用。**
        /// </summary>
        private static string ResolvePlatform()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.Android:
                    return "android";
                case RuntimePlatform.IPhonePlayer:
                    return "ios";
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor:
                    return "windows";
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor:
                    return "mac";
                case RuntimePlatform.WebGLPlayer:
                    return "web";
                default:
                    return Application.platform.ToString().ToLowerInvariant();
            }
        }

        /// <summary>
        /// 读取或创建持久化的 client_id（术语见 CONTEXT.md「客户端标识」）。
        ///
        /// <para>
        /// ⚠️ **用独立文件，不用 PlayerPrefs。** 三个理由：
        /// 小游戏平台的 PlayerPrefs 被 SDK 覆写过（要走 UmbreeyPlayerPrefs，那在别的程序集里）；
        /// 删除账号流程会 Archive.DeleteAll() 清掉整个 PlayerPrefs，而 client_id 是设备维度的、
        /// 不该跟着账号走；以及日志目录本来就要建，多一个文件不多。
        /// </para>
        /// <para>
        /// 读写失败一律降级成"本次会话内有效的临时 id"：宁可这次会话的 client_id 对不上，
        /// 也不能让日志系统起不来。
        /// </para>
        /// </summary>
        private static string LoadOrCreateClientId(string dir)
        {
            string path = Path.Combine(dir, ClientIdFileName);

            try
            {
                if (File.Exists(path))
                {
                    string text = File.ReadAllText(path).Trim();
                    Guid parsed;
                    if (Guid.TryParse(text, out parsed))
                    {
                        return parsed.ToString("D");
                    }
                }
            }
            catch (Exception)
            {
                // 读不出来就当没有，下面重新生成一个。
            }

            string clientId = Guid.NewGuid().ToString("D");
            try
            {
                File.WriteAllText(path, clientId);
            }
            catch (Exception)
            {
                // 写不进去只影响"跨会话稳定"这一个性质，本次会话照常有 id 可用。
            }

            return clientId;
        }
    }

    /// <summary>
    /// 系统自产诊断日志的事件类别，用于**限频**：同一类内部事件在
    /// <see cref="LogConfig.SuppressNoticeSec"/> 这个窗口内只放行一条
    /// （specs/0001「系统自产日志」）。
    ///
    /// <para>
    /// 限频挡的是"上传持续失败 → 每次都打一条 → 刷屏"这一类。它与重入守卫是两件事：
    /// 重入守卫是**同一次调用栈内**的递归防护，限频是**时间维度**上的节流，缺一不可。
    /// </para>
    /// <para>
    /// ⚠️ 限流的标记行与心跳**刻意不在这里列出**，它们走
    /// <see cref="Unclassified"/>（不限频）。理由：心跳的间隔已经由
    /// <see cref="LogThrottle"/> 自己按 SuppressNoticeSec 卡死了，再套一层同窗口的限频，
    /// 只会让"紧跟在一条心跳之后发生的状态跃迁标记"被吃掉 —— 而那一行恰恰是
    /// "现在连正常日志也停了"这个最需要被看到的转折。
    /// </para>
    /// </summary>
    internal enum LogInternalEvent
    {
        /// <summary>不限频。一次性的、或调用方自己已经有闩的事件用它。</summary>
        Unclassified = 0,

        /// <summary>
        /// 采集缓冲背压丢弃的计数。**必须限频**：持续超限时打包线程每被唤醒一次就会报一行，
        /// 一场异常刷屏能刷出每秒几百行。
        /// </summary>
        BufferDropped,

        /// <summary>
        /// 一次 flush 没等到确认（超时，或停机中途）。**必须限频**：磁盘一直繁忙时，
        /// 每次切后台都会来一条，而玩家可能在反复切前后台。
        /// </summary>
        FlushIncomplete,

        /// <summary>
        /// flush 那一轮里发生了写盘失败。与 <see cref="FlushIncomplete"/> **分开限频** ——
        /// 两者是完全不同的故障（一个是慢，一个是坏），
        /// 共用一个窗口会让先到的那条把另一类整整吃掉一个窗口。
        /// </summary>
        FlushWriteFailed,

        /// <summary>枚举成员计数，用作限频数组的长度。**永远放在最后。**</summary>
        Count,
    }

    // ════════════════════════════════════════════════════════════════════════════════════
    //  模块间接口 —— 冻结契约 C4。签名不得再变。
    //
    //  C4 的第三个成员 LogSigner（S2 实现、S4 调用的无状态纯函数）已由票 02 落地，
    //  就在同目录的 LogSigner.cs。它的契约签名是：
    //      internal static string Sha256Hex(byte[] payloadRaw);
    //      internal static string Sign(string appSecret, string unixSecondsText, string payloadSha256Hex);
    //      internal static byte[] Gzip(byte[] payloadRaw);
    //  该文件归 S2 独占，本票不碰它。
    //  注意它顶部已声明 [assembly: InternalsVisibleTo("Umbreey.Tests.Editor")] ——
    //  后续任何票**不要新建 AssemblyInfo.cs 重复声明**，重复会编译报错。
    // ════════════════════════════════════════════════════════════════════════════════════

    // ════════════════════════════════════════════════════════════════════════════════════
    //  生命周期 flush（票 06）
    //
    //  为什么这几个类型住在 LogSystem.cs：文件所有权表（specs/0000-index.md）把 S1 的文件
    //  逐个点了名，新建文件不在其列。就归类而言这里也是对的 ——
    //  它们是**门面的停机与 flush 机制**，天然属于生命周期的所有者。
    // ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 一次 flush 等待的全部结果。做成一个值而不是几个 out 参数，
    /// 是为了让文案生成（<see cref="LogSystem.DescribeFlush"/>）成为一个可以摆输入断输出的纯函数
    /// —— 与票 05 的 <see cref="LogThrottleDecision"/> 同一个套路。
    /// </summary>
    internal struct LogFlushResult
    {
        /// <summary>水位达标（且强制切批的请求也已被服务）。</summary>
        public bool Completed;

        /// <summary>本轮要等到的目标水位 = 调用那一刻已入缓冲的最大序号。</summary>
        public long Target;

        /// <summary>返回那一刻打包线程已确认处理到的序号。</summary>
        public long Written;

        /// <summary>本轮里卷报告过写盘失败。**它不影响 <see cref="Completed"/>** —— 见票 06。</summary>
        public bool WriteFailed;

        /// <summary>等待期间日志系统被关停，不是超时。两者的诊断文案必须分开。</summary>
        public bool Closed;

        /// <summary>本次等待的预算（毫秒），诊断文案用。</summary>
        public int WaitedForMs;

        /// <summary>
        /// 还没落盘的条数。
        /// <para>
        /// ⚠️ 它是**序号差**，因此是上界：中间可能混着被背压丢掉、本来就永远不会落盘的记录
        /// （<see cref="LogBuffer.EnqueuedSeq"/>）。宁可报得略高，
        /// 也不能让人以为"一条都没丢"。
        /// </para>
        /// </summary>
        public long Pending
        {
            get { return Target > Written ? Target - Written : 0L; }
        }
    }

    /// <summary>
    /// flush 的**序号水位**闸门（票 06）。主线程在这里等，打包线程在这里报。
    ///
    /// <para>
    /// ⚠️ **为什么是水位而不是布尔信号** —— 这是本票最容易做错的一处。
    /// 用 <c>AutoResetEvent</c> 之类的布尔信号时：第一次 flush 超时放弃，
    /// 打包线程随后（比如磁盘忙完了）才把信号置位；这个信号**没人消费，就一直留在那儿**。
    /// 下一次 flush 一进来立刻被它满足，于是明明有一批记录还在缓冲里，Flush 却报成功 ——
    /// 而调用它的正是 pause，紧接着进程就被杀了。
    /// </para>
    /// <para>
    /// 水位没有这个失效模式，靠的是两条性质叠在一起：
    /// </para>
    /// <para>
    /// 1. <see cref="m_Written"/> 是**单调不减的量**，不是"有/无"的信号。迟到的那次
    ///    <see cref="ReportRound"/> 带着的是旧的序号，把水位推到旧的高度；而下一轮 flush
    ///    的目标是**当时重新读的**一个更高的号（新记录已经领了新号），旧水位满足不了它。
    /// </para>
    /// <para>
    /// 2. 等待用 <c>Monitor.Wait</c> + **循环复检谓词**，不是"被唤醒就算数"。
    ///    迟到的 <c>PulseAll</c> 至多让等待方醒一次、发现 <c>m_Written &lt; target</c>、接着睡 ——
    ///    它无法把一次等待"消费"掉。
    /// </para>
    /// <para>
    /// 强制切批（pause 的 outbox 批次）用的是同一套办法：递增的**票号**，不是布尔标志，
    /// 理由完全相同。
    /// </para>
    /// <para>
    /// ⚠️ **本类不读墙上时钟**：等待用 <c>Stopwatch</c> 度量剩余预算（全局硬约束 2），
    /// 超时预算由调用方按 <see cref="LogConfig.FlushWaitMs"/> 注入。
    /// 不碰 IO、不碰 Unity API，因此可以在 EditMode 里直接摆输入断输出（与票 05 的
    /// <see cref="LogThrottle"/> 同形）。
    /// </para>
    /// </summary>
    internal sealed class LogFlushGate
    {
        /// <summary>
        /// "本轮把所有待处理的强制切批请求都服务掉了"。停机排空（FinalDrain）用它 ——
        /// 那一轮确实把攒批的尾巴也固化了，没有任何请求应该继续等下去。
        /// </summary>
        internal const long ServeAllForceTickets = long.MaxValue;

        /// <summary>既是互斥锁，也是 <c>Monitor.Wait</c> / <c>PulseAll</c> 的条件变量。</summary>
        private readonly object m_Lock = new object();

        /// <summary>打包线程已确认处理完的最大序号。**单调不减。**</summary>
        private long m_Written;

        /// <summary>最近一次强制切批请求的票号。主线程递增。</summary>
        private long m_ForceRequested;

        /// <summary>已经被打包线程服务掉的最大票号。**单调不减。**</summary>
        private long m_ForceServed;

        /// <summary>
        /// 自上次被 <see cref="Wait"/> 取走以来，卷报告过写盘失败。
        /// 取走即清零 —— 它描述的是"这一轮"，不是会话累计。
        /// </summary>
        private bool m_WriteFailed;

        /// <summary>系统已停机：不会再有人来报水位了，等待方必须立刻被放走。</summary>
        private bool m_Closed;

        /// <summary>当前水位。诊断与测试用。</summary>
        internal long Written
        {
            get { lock (m_Lock) { return m_Written; } }
        }

        /// <summary>已停机。</summary>
        internal bool IsClosed
        {
            get { lock (m_Lock) { return m_Closed; } }
        }

        /// <summary>
        /// 主线程：请求打包线程下一轮**强制切一个批出去**（pause）。
        /// </summary>
        /// <returns>票号。等待方拿它判定"我这次的请求被服务了没有"。</returns>
        internal long RequestForceBatch()
        {
            lock (m_Lock)
            {
                m_ForceRequested++;
                return m_ForceRequested;
            }
        }

        /// <summary>
        /// 打包线程：本轮要服务的强制切批票号，没有待处理的请求就是 0。
        /// <para>
        /// ⚠️ 这里**不清空 <see cref="m_ForceRequested"/>**，票号在
        /// <see cref="ReportRound"/> 里才算服务完成。中途出异常的话，请求还在，下一轮接着服务。
        /// </para>
        /// </summary>
        internal long TakeForceBatch()
        {
            lock (m_Lock)
            {
                return m_ForceRequested > m_ForceServed ? m_ForceRequested : 0L;
            }
        }

        /// <summary>
        /// 打包线程：报告"序号 ≤ <paramref name="seq"/> 的记录本轮都处理完了"。
        ///
        /// <para>
        /// ⚠️ **写盘失败也要报**（<paramref name="writeSucceeded"/> = false）。
        /// 失败时不推进水位的话，Flush 只能一直等到超时 —— 而磁盘已经坏了，
        /// 等下去不会有任何好转。"这一轮结束了"和"这一轮成功了"是两件事，
        /// 前者决定要不要放人走，后者决定要不要留一条诊断（票 06）。
        /// </para>
        /// </summary>
        /// <param name="servedForceTicket">
        /// 本轮服务掉的强制切批票号（<see cref="TakeForceBatch"/> 的返回值），没有就传 0。
        /// </param>
        internal void ReportRound(long seq, bool writeSucceeded, long servedForceTicket)
        {
            lock (m_Lock)
            {
                if (seq > m_Written)
                {
                    m_Written = seq;
                }

                if (servedForceTicket > m_ForceServed)
                {
                    m_ForceServed = servedForceTicket;
                }

                if (!writeSucceeded)
                {
                    m_WriteFailed = true;
                }

                Monitor.PulseAll(m_Lock);
            }
        }

        /// <summary>
        /// 主线程：等到水位 ≥ <paramref name="target"/>（且强制切批票已被服务），或者超时。
        ///
        /// <para>
        /// 循环里**每次醒来都复检谓词**：这正是"迟到的确认不能误满足下一轮"的第二道保证。
        /// 被唤醒本身不代表任何事，只有 <see cref="m_Written"/> 真的到位才算数。
        /// </para>
        /// </summary>
        /// <param name="target">目标水位 = 调用那一刻缓冲的 <see cref="LogBuffer.EnqueuedSeq"/>。</param>
        /// <param name="forceTicket">强制切批的票号，没请求就传 0。</param>
        /// <param name="timeoutMs">等待预算。调用方已经把 -1 翻译成 FlushWaitMs（契约 C5 补记）。</param>
        internal LogFlushResult Wait(long target, long forceTicket, int timeoutMs)
        {
            if (timeoutMs < 0)
            {
                // 这里**不再**把负数解释成"用 FlushWaitMs"或"无限等待"：
                // 一个值只该有一种含义，翻译发生在 LogSystem.FlushInternal 那一处。
                timeoutMs = 0;
            }

            // 单调时钟。墙上时钟会被玩家改系统时间污染，用它算剩余预算能算出负数（全局硬约束 2）。
            System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();

            lock (m_Lock)
            {
                while (!IsSatisfiedLocked(target, forceTicket) && !m_Closed)
                {
                    long remain = timeoutMs - elapsed.ElapsedMilliseconds;
                    if (remain <= 0L)
                    {
                        break;
                    }

                    Monitor.Wait(m_Lock, (int)remain);
                }

                LogFlushResult result = new LogFlushResult
                {
                    Completed = IsSatisfiedLocked(target, forceTicket),
                    Target = target,
                    Written = m_Written,
                    WriteFailed = m_WriteFailed,
                    Closed = m_Closed,
                    WaitedForMs = timeoutMs,
                };

                // 取走即清零：它描述的是刚刚这一轮。
                m_WriteFailed = false;
                return result;
            }
        }

        /// <summary>
        /// 停机：放走所有等待方。**幂等**，重复调用不抛（退出流程与域重载会走同一条路径）。
        /// </summary>
        internal void Close()
        {
            lock (m_Lock)
            {
                m_Closed = true;
                Monitor.PulseAll(m_Lock);
            }
        }

        /// <summary>谓词。调用方已持锁。</summary>
        private bool IsSatisfiedLocked(long target, long forceTicket)
        {
            return m_Written >= target && m_ForceServed >= forceTicket;
        }
    }

    /// <summary>
    /// **有界停止顺序**的五个步骤（票 06）。
    /// 抽成接口是为了让顺序本身能在 EditMode 里被验证 —— 顺序错了不会有编译错误、
    /// 也不会有异常，只会在真机上表现成"退出时最后一段日志没了"或"退出卡 10 秒"。
    /// </summary>
    internal interface ILogStopSteps
    {
        /// <summary>① 停止接受新日志：摘采集钩子，让 flush 有一个不会再涨的目标。</summary>
        void StopAccepting();

        /// <summary>② flush：等打包线程把缓冲里的写完。主线程只等，不自己写盘。</summary>
        void FlushToDisk();

        /// <summary>③ 停打包线程：它退出前做最后一次排空并关卷。Join 必须有界。</summary>
        void StopPackThread();

        /// <summary>④ 停上传线程：<c>Stop</c> 必须能取消在飞的 HTTP，不能等它自己超时。</summary>
        void StopUploader();

        /// <summary>⑤ 关句柄：任何路径都必须走到，泄漏句柄下次就打不开卷。</summary>
        void CloseHandles();
    }

    /// <summary>
    /// 停止顺序（票 06）：**停止接受新日志 → flush → 停打包线程 → 停上传线程 → 关句柄**。
    ///
    /// <para>
    /// 每一步都各自 try/catch：**前面一步失败不能拖住后面的**。
    /// 最要紧的是最后那步 —— 前面出什么事都得把句柄关掉，否则下次进播放模式打不开卷。
    /// </para>
    /// <para>
    /// 纯逻辑，不碰 Unity API、不碰 IO、不读时钟，可在 EditMode 里用假实现验证顺序。
    /// </para>
    /// </summary>
    internal static class LogStopSequence
    {
        internal static void Run(ILogStopSteps steps)
        {
            if (steps == null)
            {
                return;
            }

            Step(steps.StopAccepting);
            Step(steps.FlushToDisk);
            Step(steps.StopPackThread);
            Step(steps.StopUploader);
            Step(steps.CloseHandles);
        }

        private static void Step(Action step)
        {
            try
            {
                step();
            }
            catch (Exception e)
            {
                // 停机路径上唯一还能做的就是说一声，然后继续往下走。
                LogSystem.InternalError("stop step failed: " + e.Message);
            }
        }
    }

    /// <summary>
    /// 上传接收端（契约 C4）。**S3 实现，S1 调用。**
    /// <para>
    /// 实现方负责按条数 / 字节 / CtxVer 切批，S1 只管把整段交出去 —— S1 不做逐条分组。
    /// 其中 CtxVer 切批是硬要求：一个批次只能包含 CtxVer 相同的记录，否则登录前的日志
    /// 会被打上登录后的 uid（契约 C2）。
    /// </para>
    /// <para>⚠️ 由**打包线程**调用，实现方不得调用任何 Unity API（全局硬约束 1）。</para>
    /// <para>
    /// ⚠️ 传进来的列表由调用方新建、交出后不再改动，实现方可以安全持有它。
    /// </para>
    /// </summary>
    internal interface ILogBatchSink
    {
        void Write(IReadOnlyList<LogRecord> records);
    }

    /// <summary>
    /// **可选**的附加接缝（票 06）：把累加中的内容立刻固化到磁盘。
    ///
    /// <para>
    /// ⚠️ 这是**新增**的可选接口，不是对契约 C4 的修改 —— <see cref="ILogBatchSink"/>
    /// 的签名一个字没动，不实现本接口的 sink 照常工作
    /// （<see cref="LogSystem.PersistSink"/> 用 <c>as</c> 探测）。
    /// 但它确实是一条 S1↔S3 之间的新约定，**需要在 specs/0000-index.md 的 C4 里补一条注记**。
    /// </para>
    /// <para>
    /// 为什么非要有它：pause 时"强制产出一个 outbox 批次"要求的是**字节到了磁盘上**。
    /// S1 这边把整段交出去（<see cref="ILogBatchSink.Write"/>）只完成了一半 ——
    /// 按 C4，切批是 S3 的职责，S3 手里很可能还留着一个没切完的尾巴，
    /// 那部分不落盘，进程被杀就没了。
    /// </para>
    /// <para>
    /// ⚠️ 由**打包线程**调用（写盘只在这一个线程上发生），实现方不得调用任何 Unity API，
    /// 且**绝不能在这里发网络请求** —— 它的调用点之一是 pause 回调所等待的那一轮
    /// （协议 §4.5 / ANR）。
    /// </para>
    /// </summary>
    internal interface ILogBatchSinkPersist
    {
        void Persist();
    }

    /// <summary>
    /// 上传器（契约 C4）。**S4 实现，S1 负责启停。**
    /// <para>⚠️ 运行在**上传线程**上，实现方不得调用任何 Unity API（全局硬约束 1）。</para>
    /// </summary>
    internal interface ILogUploader
    {
        void Start();

        /// <summary>
        /// 停上传线程。
        /// <para>
        /// ⚠️ **必须立刻返回，并取消在飞的请求**（票 06 的停止顺序对它的要求）。
        /// 一个 <see cref="LogConfig.HttpTimeoutMs"/>（10s）的 HTTP 挂着不管，
        /// 玩家退出游戏就要等 10 秒。实现方（S4，票 09）在 Stop 里 cancel，
        /// **不能**"等它自己超时"。
        /// </para>
        /// <para>这是对 C4 已有签名的**语义补充**，签名本身没有变化。</para>
        /// </summary>
        void Stop();

        /// <summary>null / 空数组 = 停发，但不影响打包。</summary>
        void SetAddresses(string[] addrs);

        /// <summary>网络恢复：重置所有批次的重试预算（预算耗尽是暂停，不是删除）。</summary>
        void OnNetworkResumed();
    }

    /// <summary>
    /// 什么也不做的 <see cref="ILogBatchSink"/>。真实实现（S3）缺席时的兜底 ——
    /// 有了它，S1 不必到处判空，也能独立编译、独立跑通本地卷部分（契约 C4 末段）。
    /// 无状态，可安全共享同一个实例。
    /// </summary>
    internal sealed class NullLogBatchSink : ILogBatchSink, ILogBatchSinkPersist
    {
        internal static readonly NullLogBatchSink Instance = new NullLogBatchSink();

        private NullLogBatchSink()
        {
        }

        /// <summary>丢弃整段记录。日志已经写进本地卷了，这里不写就只是不上传。</summary>
        public void Write(IReadOnlyList<LogRecord> records)
        {
        }

        /// <summary>
        /// 空实现（票 06 / 08）。pause 的强制切批在 08 合入之前落在这里，什么也不会发生 ——
        /// 但**调用顺序已经是最终形态**：08 只需要让 S3 的 sink 实现同一个接口。
        /// </summary>
        public void Persist()
        {
        }
    }

    /// <summary>
    /// 什么也不做的 <see cref="ILogUploader"/>。真实实现（S4）缺席时的兜底，
    /// 也是 Standalone / Editor / WebGL 上"只采集不上传"这条平台矩阵行的天然落点
    /// （全局硬约束 3）。无状态，可安全共享同一个实例。
    /// </summary>
    internal sealed class NullLogUploader : ILogUploader
    {
        internal static readonly NullLogUploader Instance = new NullLogUploader();

        private NullLogUploader()
        {
        }

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void SetAddresses(string[] addrs)
        {
        }

        public void OnNetworkResumed()
        {
        }
    }
}
