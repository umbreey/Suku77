namespace Umbreey
{
    /// <summary>
    /// 日志系统的全部可调阈值。接入方可在 <see cref="LogSystem.Init"/> 时整体覆盖。
    ///
    /// <para>
    /// ⚠️ **默认值的唯一事实源是 specs/0000-index.md 的「参数总表」。**
    /// 本类是那张表在代码里的唯一落点 —— 除此之外任何地方都不许再出现这些数值的字面量。
    /// 之前正是因为多份文档各自复写了一份数值，产生了三个互相矛盾的版本。
    /// 要改某个阈值：先改参数总表，再改这里，两处必须同步。
    /// </para>
    ///
    /// <para>
    /// ⚠️ **appId / appSecret / topic 不在这里**，它们是 <see cref="LogSystem.Init"/> 的显式参数
    /// （全局硬约束 4）—— 避免 secret 混进一个可能被整体 dump 的配置对象。
    /// 同理**本类刻意不覆写 ToString()**：留一个"把所有字段打印出来"的方法，
    /// 就等于给未来某次"顺手打条配置日志"埋雷。
    /// </para>
    ///
    /// <para>
    /// 本类只是一袋子数值，不含任何逻辑、不碰 Unity API，打包线程与上传线程可以直接读
    /// （全局硬约束 1）。约定：<see cref="LogSystem.Init"/> 之后不再修改实例，
    /// 因此跨线程只读无需同步。
    /// </para>
    ///
    /// <para>
    /// 可见性说明：本类是 public 而不像其余核心类型那样 internal —— 它是公开 API
    /// <see cref="LogSystem.Init"/> 的参数类型，宿主（Scripts 程序集）要能 new 出来并逐项覆盖。
    /// C# 不允许 public 方法带 internal 参数类型（CS0051）。
    /// </para>
    ///
    /// <para>
    /// 时间单位一律写进字段名（Ms / Sec / Days）。凡是**时间间隔**的度量都必须用单调时钟
    /// （Stopwatch），不用墙上时钟（全局硬约束 2）—— 这些字段只是阈值，怎么计时是使用方的责任。
    /// </para>
    /// </summary>
    public sealed class LogConfig
    {
        // ================================================================================
        //  与协议对齐的部分（11 项）—— 参数总表「与协议对齐的部分」
        // ================================================================================

        /// <summary>
        /// 攒批时间窗口。参数总表：3000ms（协议建议 5 秒，这里收紧，协议允许）。
        /// <para>
        /// 只服务于上传接收端。**本地卷不参与攒批**，每次缓冲交换就立即写盘（ADR-0001）——
        /// 把这 3 秒强加给本地文件，等于让本地日志替上传的成本买单。
        /// </para>
        /// <para>打包线程的等待超时 = 距离下一次攒批截止还剩多久，不是固定间隔轮询。</para>
        /// </summary>
        public int BatchLingerMs = 3000;

        /// <summary>攒批条数上限。参数总表：100（协议建议 100 / schema 硬限 200）。</summary>
        public int BatchMaxCount = 100;

        /// <summary>
        /// 攒批字节上限（**压缩前**的签名载荷字节）。参数总表：256KB，与协议建议一致。
        /// </summary>
        public int BatchMaxBytes = 256 * 1024;

        /// <summary>单条日志的字节上限。参数总表：64KB，等于协议硬限。超出由 S3 截断。</summary>
        public int MsgMaxBytes = 64 * 1024;

        /// <summary>
        /// gzip 阈值：签名载荷小于它就不压缩。参数总表：1KB，与协议一致。
        /// <para>阈值判断是调用方（S4）的职责，LogSigner.Gzip 无条件压缩。</para>
        /// </summary>
        public int GzipMinBytes = 1024;

        /// <summary>退避起始间隔。参数总表：1s，与协议一致（协议另要求 jitter）。</summary>
        public int BackoffBeginMs = 1000;

        /// <summary>退避上限间隔。参数总表：60s，与协议一致。</summary>
        public int BackoffMaxMs = 60 * 1000;

        /// <summary>
        /// 每次连通性事件的重试预算。参数总表：5 次。
        /// <para>⚠️ 预算耗尽是**暂停不删除** —— 批次留在 outbox 里等下一次网络恢复，不能丢。</para>
        /// </summary>
        public int RetryMax = 5;

        /// <summary>
        /// 重签名阈值：批次年龄超过它就要重算签名。参数总表：250s（协议窗口 300s，留 50s 余量）。
        /// <para>
        /// ⚠️ 年龄判定**必须用单调时钟**，不能用 outbox 文件名里的 createUnixSec ——
        /// 文件名时间会被系统时钟回拨污染（契约 C3）。
        /// </para>
        /// </summary>
        public int ResignAfterSec = 250;

        /// <summary>
        /// outbox 目录的容量上限。参数总表：50MB（协议建议 30MB，可自定，这里放宽）。
        /// <para>long：使用方是"目录内文件大小累加"，避免调用方为比较写强制转换。</para>
        /// </summary>
        public long OutboxMaxBytes = 50L * 1024 * 1024;

        /// <summary>
        /// outbox 文件的 TTL。参数总表：7 天。
        /// <para>天数判定用文件名里的 createUnixSec（墙上时钟的三个合法用途之一，全局硬约束 2）。</para>
        /// </summary>
        public int OutboxKeepDays = 7;

        // ================================================================================
        //  客户端自有的部分（12 项）—— 参数总表「客户端自有的部分（协议不管）」
        // ================================================================================

        /// <summary>
        /// 采集缓冲的背压上限。参数总表：2MB。超限**丢最旧**的记录（最靠近崩溃的是最新的）。
        /// <para>
        /// ⚠️ 在缓冲里被丢的记录**还没写过本地卷**，两个接收端会同时失去它（ADR-0001）。
        /// 不要宣称"被丢的旧记录仍然在本地卷里"。
        /// </para>
        /// </summary>
        public int BufferLimitBytes = 2 * 1024 * 1024;

        /// <summary>
        /// 单会话日志总字节上限。参数总表：20MB。
        /// <para>
        /// 这是**磁盘兜底，不是实时保护**（每帧一条 Info 要 37 分钟才触顶）；
        /// 真正挡失控的实时闸是 <see cref="ExceptionLimitCount"/>。两者作用不同，不要合并。
        /// 触顶后**全停**。
        /// </para>
        /// <para>long：使用方是会话累计字节数。</para>
        /// </summary>
        public long SessionLimitBytes = 20L * 1024 * 1024;

        /// <summary>
        /// 单会话异常条数上限。参数总表：1000 条。
        /// <para>
        /// **真正的实时保护**（每帧抛异常十几秒就触顶）。触顶后只停 Error/Assert/Exception，
        /// 正常 Info/Warning 继续 —— 两层计数彼此独立。
        /// </para>
        /// </summary>
        public int ExceptionLimitCount = 1000;

        /// <summary>
        /// 限流抑制期内的心跳间隔。参数总表：60s。
        /// <para>
        /// 每隔这么久放行一条，保留"还在持续报错"的信号。全静默会让人在 CLS 里看到
        /// "日志突然断了"却分不清是崩溃还是限流。
        /// </para>
        /// <para>⚠️ 心跳与限流标记行本身是 Internal 日志，**bypass 限流计数**（全局硬约束 6）。</para>
        /// <para>⚠️ 间隔用单调时钟度量（全局硬约束 2）。</para>
        /// </summary>
        public int SuppressNoticeSec = 60;

        /// <summary>
        /// 单条堆栈的截断上限。参数总表：16KB。协议未规定，防爆栈撑破批次。
        /// </summary>
        public int StackMaxBytes = 16 * 1024;

        /// <summary>
        /// 单卷上限。参数总表：20MB，**与 <see cref="SessionLimitBytes"/> 对齐** ——
        /// 这正是"一次会话永不跨卷"的保证所在，也是"发一个卷 == 发完整一次运行"成立的前提。
        /// <para>卷满属于**硬停**：之后本地卷完全静默（心跳也不写卷），但仍进 outbox。</para>
        /// <para>long：使用方是当前卷的累计字节数。</para>
        /// </summary>
        public long VolumeMaxBytes = 20L * 1024 * 1024;

        /// <summary>
        /// 保留的历史卷数量上限。参数总表：60 个。
        /// <para>三条保留规则谁先到删谁；**当前会话的卷豁免全部规则**。</para>
        /// </summary>
        public int VolumeKeepCount = 60;

        /// <summary>
        /// 历史卷的总字节预算。参数总表：60MB。
        /// <para>
        /// ⚠️ 它是**历史卷预算，不是运行期硬上限**：清理只在启动时做一次，之后当前卷会继续
        /// 增长到 <see cref="VolumeMaxBytes"/>。所以磁盘峰值 = LocalMaxBytes + VolumeMaxBytes。
        /// 这是有意选择（避免每次写卷都扫目录），但不要宣称两者等价。
        /// </para>
        /// <para>long：使用方是目录内文件大小累加。</para>
        /// </summary>
        public long LocalMaxBytes = 60L * 1024 * 1024;

        /// <summary>
        /// 卷的保留天数。参数总表：7 天。
        /// <para>
        /// 天数**从卷名解析**，不用文件 mtime（mtime 会被文件拷贝污染）。
        /// 这是墙上时钟的三个合法用途之一（全局硬约束 2）。
        /// </para>
        /// </summary>
        public int VolumeKeepDays = 7;

        /// <summary>
        /// <see cref="LogSystem.Flush"/> 的默认等待上限。参数总表：300ms。
        /// <para>
        /// 按最坏情况定：正常一次 flush 只要几毫秒，这个值只在磁盘繁忙时起作用，
        /// 而那正是最该多等的时候。
        /// </para>
        /// </summary>
        public int FlushWaitMs = 300;

        /// <summary>
        /// Attach 之后多久仍未 Init 就打一条 Error。参数总表：30s。
        /// <para>"业务忘了调 Init"的表现是"CLS 里没日志"，非常难查，必须自己喊出来。</para>
        /// </summary>
        public int InitWarnSec = 30;

        /// <summary>单次上传请求的超时。参数总表：10s。</summary>
        public int HttpTimeoutMs = 10 * 1000;
    }
}
