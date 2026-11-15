using System;

namespace Umbreey
{
    // ════════════════════════════════════════════════════════════════════════════════════
    //  S4 · 上报地址的会话内选址状态机（票 10 / specs 0004「地址」/ 协议 §4.2）
    //
    //  规则四条，与协议 §4.2 逐字对应：
    //    1. 优先用列表第一个
    //    2. 连不上 → 换下一个
    //    3. 成功后**粘住本次会话**，不要每批重新选
    //    4. 全部失败 → 回到第一个，重新开始一轮
    //
    //  ⚠️ **没有内置兜底域名，也不缓存"上次成功地址"**（票 10 / specs/0004 的明确取舍）。
    //  地址只能来自 dispatch，**已知代价并明确接受**：一个从来没成功走通过 dispatch 的玩家，
    //  他的日志永远上不来 —— 而这类玩家（连不上服务器、卡在启动）恰恰最需要被诊断。
    //  下个版本再补兜底域名，**实现时不要"顺手"加回来**：加一个常量域名很容易，
    //  但它会把"日志上报"悄悄变成一条绕过 dispatch 的固定出口，那是另一个决定。
    //
    //  现成范例平移自 ConnectMod 的网关选择（m_GateIndex / m_GateIsLocked /
    //  TryNextGate / ResetGate）。⚠️ **有一处刻意不同**：ConnectMod 里 locked 之后
    //  TryNextGate 直接 return（连上过的网关永不切换），而票 10 明确要求
    //  "粘住后失败 → 换下一个"。所以这里的 Sticky 不参与切换决策，见 <see cref="Sticky"/>。
    // ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 契约 C4 的地址来源（票 09 定义的 <see cref="ILogUploadAddresses"/>）的真实实现，
    /// 取代票 09 的半成品 <see cref="LogUploadFirstAddress"/>。
    ///
    /// <para>
    /// 状态只有两个字段：**当前下标**与**是否成功过**。"粘住"不需要额外机制 ——
    /// 下标本身是会话内持久的，上传循环每批只问 <see cref="Current"/>，从不重新选址。
    /// </para>
    /// <para>
    /// ⚠️ 线程：<see cref="Set"/> 由**主线程**调用（<c>EnableUpload</c> ← dispatch 回调），
    /// 其余四个成员由**上传线程**调用。地址数组与下标必须整体一致
    /// （下标越界会让上传线程抛异常，而那表现成"CLS 里再也没有日志"），故整体加锁。
    /// 锁只护三个内存字段，不护任何 IO，竞争实测为零（一次会话里 Set 通常只发生一次）。
    /// </para>
    /// <para>⚠️ 本类不打任何日志：地址切换的诊断由上传循环统一报（它才知道"这一批发给了谁"）。</para>
    /// </summary>
    internal sealed class LogAddressBook : ILogUploadAddresses
    {
        /// <summary>批次上报的路径（协议 §3.1）。</summary>
        internal const string BatchPath = "/v1/logs/batch";

        /// <summary>免签名时间诊断接口的路径（协议 §3.7）。</summary>
        internal const string TimePath = "/v1/time";

        private static readonly Endpoint[] None = new Endpoint[0];

        private readonly object m_Lock = new object();

        private Endpoint[] m_Endpoints = None;

        /// <summary>当前下标。**永远落在 <c>[0, m_Endpoints.Length)</c>**（空列表时为 0 且不被读）。</summary>
        private int m_Index;

        private bool m_Sticky;

        /// <summary>
        /// 收下地址列表（契约 C4 / 协议 §4.2）。
        ///
        /// <para>
        /// ⚠️ **null / 空数组 = 停发**，按**临时降级**处理：**打包继续**，
        /// 日志继续进 outbox 等地址恢复后补传，靠 <see cref="LogConfig.OutboxMaxBytes"/> 自然约束。
        /// 服务端故障远比"永久关闭"常见，所以这里绝不能顺手把本地链路也停掉
        /// （本地卷根本不经过本类，天然不受影响）。
        /// </para>
        /// <para>
        /// 空白项（<c>""</c> / 纯空格）不算地址，**逐项剔除而不是整列表作废** ——
        /// dispatch 的列表里混进一个空串不该把另外两个好地址一起废掉。
        /// </para>
        /// <para>
        /// **同一份列表重复下发时保留当前下标与粘住状态**：dispatch 刷新、或宿主重复调
        /// <c>EnableUpload</c> 都会走到这里，若无条件回到第 0 个，就等于"每次刷新都重新
        /// 试一遍已知失败的地址"，粘住的意义被抹掉。列表**内容或顺序变了**才是新一轮，
        /// 此时回到第 0 个（服务端换了名单，旧下标没有任何意义）。
        /// </para>
        /// </summary>
        public void Set(string[] addrs)
        {
            Endpoint[] next = Build(addrs);

            lock (m_Lock)
            {
                if (SameList(m_Endpoints, next))
                {
                    return;
                }

                m_Endpoints = next;
                m_Index = 0;
                m_Sticky = false;
            }
        }

        /// <summary>
        /// 有可用地址。false 时上传线程**休眠**，⚠️ **不做退避重试** ——
        /// 没有东西可以重试，空转只会烧电（票 10 的两种"发不出去"必须区别对待）。
        /// </summary>
        public bool HasAddress
        {
            get { lock (m_Lock) { return m_Endpoints.Length > 0; } }
        }

        /// <summary>当前该用的批次上报 URL；没有地址时为 null。</summary>
        public string Current
        {
            get
            {
                lock (m_Lock)
                {
                    return m_Endpoints.Length > 0 ? m_Endpoints[m_Index].BatchUrl : null;
                }
            }
        }

        /// <summary>
        /// 当前地址上的 <c>GET /v1/time</c>（协议 §3.7）；没有地址时为 null。
        /// <para>
        /// 与 <see cref="Current"/> **同源同一个地址** —— 校时必须问"正在拒绝我们的那台服务器"，
        /// 问另一台等于拿 A 的时间去对 B 的窗口。
        /// </para>
        /// </summary>
        public string CurrentTimeUrl
        {
            get
            {
                lock (m_Lock)
                {
                    return m_Endpoints.Length > 0 ? m_Endpoints[m_Index].TimeUrl : null;
                }
            }
        }

        /// <summary>
        /// 发送成功 → **粘住本次会话**（协议 §4.2 第 3 条）。
        /// <para>下标本来就不会自己动，所以这里只需记下"这个地址是通的"，见 <see cref="Sticky"/>。</para>
        /// </summary>
        public void ReportSuccess()
        {
            lock (m_Lock)
            {
                if (m_Endpoints.Length > 0)
                {
                    m_Sticky = true;
                }
            }
        }

        /// <summary>
        /// 连不上 → **换下一个**；走完一圈自然回到第 0 个重新开始一轮（协议 §4.2 第 2、4 条）。
        ///
        /// <para>
        /// ⚠️ 取模而不是"到末尾就停"：单元素列表失败后仍然是它自己（<c>(0+1)%1 == 0</c>），
        /// **不越界、也不会进入"无地址"状态** —— 只有 dispatch 给空列表才是无地址。
        /// </para>
        /// <para>
        /// ⚠️ 调用方只在**节点级失败**时调它：没拿到 HTTP 响应，或
        /// <c>code ∈ {3, 4, 9}</c>（单节点限流 / 这台过载 / 这台内部错误）。
        /// <c>code ∈ {1, 2, 5}</c>、畸形 body、未知 code 都是**请求级**失败，换哪台都一样，
        /// 不调它。判定与全部理由在 <see cref="LogUploadPolicy.ShouldSwitchAddress"/>。
        /// </para>
        /// </summary>
        public void ReportFailure()
        {
            lock (m_Lock)
            {
                int count = m_Endpoints.Length;
                if (count == 0)
                {
                    return;
                }

                m_Index = (m_Index + 1) % count;
                m_Sticky = false;
            }
        }

        /// <summary>地址条数。诊断与测试用。</summary>
        internal int Count
        {
            get { lock (m_Lock) { return m_Endpoints.Length; } }
        }

        /// <summary>当前下标。诊断与测试用。</summary>
        internal int Index
        {
            get { lock (m_Lock) { return m_Index; } }
        }

        /// <summary>
        /// 当前地址成功过。
        /// <para>
        /// ⚠️ **它不参与切换决策**，只是可观测状态（真机验收要能看出"粘住"是否成立），
        /// 外加一个作用：同一份列表重复下发时据此说明"当前下标是有来由的"。
        /// 与 ConnectMod 的 <c>m_GateIsLocked</c> 同名不同权 —— 那边 locked 之后永不切换，
        /// 而票 10 明确要求"粘住后失败 → 换下一个"（日志上报没有"重连同一个网关"这回事：
        /// 一个批次发不出去就该换一台，而不是守着一台等）。
        /// </para>
        /// </summary>
        internal bool Sticky
        {
            get { lock (m_Lock) { return m_Sticky; } }
        }

        /// <summary>
        /// 把 dispatch 下发的一条地址拼成**批次上报**的完整 URL。
        /// <para>
        /// 地址可能是域名（公网 HTTPS）也可能是 IP（内网直连）——
        /// **直接用，不做 IP↔域名转换、不做解析缓存**（协议 §4.2）。
        /// 没写 scheme 的补 https；已经指到具体接口的照用（dispatch 有可能直接下发完整 URL）。
        /// </para>
        /// </summary>
        /// <returns>不可用的地址（null / 空白）返回 null。</returns>
        internal static string BuildBatchUrl(string addr)
        {
            string root;
            string path;
            if (!Split(addr, out root, out path))
            {
                return null;
            }

            return path == null ? root + BatchPath : root + path;
        }

        /// <summary>
        /// 把同一条地址拼成 <c>GET /v1/time</c> 的完整 URL（协议 §3.7）。
        /// <para>
        /// dispatch 下发的是"只给 host"还是"完整的批次 URL"都要能处理：
        /// 后者把末尾的 <see cref="BatchPath"/> 摘掉再换上 <see cref="TimePath"/>，
        /// 这样 <c>https://h/silog/v1/logs/batch</c> 这种带前缀的部署也能拼对
        /// （→ <c>https://h/silog/v1/time</c>）。
        /// 路径完全认不出来时回落到 host 根 —— 那是协议文档里 <c>/v1/time</c> 的标准位置。
        /// </para>
        /// </summary>
        internal static string BuildTimeUrl(string addr)
        {
            string root;
            string path;
            if (!Split(addr, out root, out path))
            {
                return null;
            }

            if (path == null)
            {
                return root + TimePath;
            }

            if (path.EndsWith(BatchPath, StringComparison.OrdinalIgnoreCase))
            {
                return root + path.Substring(0, path.Length - BatchPath.Length) + TimePath;
            }

            return root + TimePath;
        }

        /// <summary>
        /// 归一化一条地址：补 scheme、去掉末尾的 <c>/</c>，拆成 <c>root</c>（scheme + 权威段）
        /// 与 <c>path</c>（含前导 <c>/</c>，没有路径时为 null）。
        /// </summary>
        private static bool Split(string addr, out string root, out string path)
        {
            root = null;
            path = null;

            if (string.IsNullOrEmpty(addr))
            {
                return false;
            }

            string text = addr.Trim();
            if (text.Length == 0)
            {
                return false;
            }

            if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                text = "https://" + text;
            }

            // scheme 先切下来再动末尾的斜杠 —— 反过来做的话 "https://" 这种残缺地址会被
            // TrimEnd 削成 "https:"，然后被当成一个合法的 host 拼出 "https:/v1/logs/batch"。
            int schemeEnd = text.IndexOf("://", StringComparison.Ordinal) + 3;
            string scheme = text.Substring(0, schemeEnd);

            // 末尾的 '/' 会被误判成"带路径"，于是 "https://host/" 拼不出任何接口路径。
            string rest = text.Substring(schemeEnd).TrimEnd('/');
            if (rest.Length == 0)
            {
                // 只有 scheme，没有 host。
                return false;
            }

            int slash = rest.IndexOf('/');
            if (slash < 0)
            {
                root = scheme + rest;
                return true;
            }

            root = scheme + rest.Substring(0, slash);
            path = rest.Substring(slash);
            return true;
        }

        private static Endpoint[] Build(string[] addrs)
        {
            if (addrs == null || addrs.Length == 0)
            {
                return None;
            }

            var usable = new Endpoint[addrs.Length];
            int count = 0;

            for (int i = 0; i < addrs.Length; i++)
            {
                string batch = BuildBatchUrl(addrs[i]);
                if (batch == null)
                {
                    continue;
                }

                usable[count++] = new Endpoint(batch, BuildTimeUrl(addrs[i]));
            }

            if (count == 0)
            {
                return None;
            }

            if (count == usable.Length)
            {
                return usable;
            }

            var trimmed = new Endpoint[count];
            Array.Copy(usable, trimmed, count);
            return trimmed;
        }

        private static bool SameList(Endpoint[] a, Endpoint[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (!string.Equals(a[i].BatchUrl, b[i].BatchUrl, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 一条地址的两个 URL。**不可变** —— 与契约 C2 的快照做法同形：
        /// 主线程构造新数组后整体换引用，上传线程只读，也避免了调用方之后改动
        /// <c>EnableUpload</c> 的那个实参（它来自网络层）。
        /// </summary>
        private sealed class Endpoint
        {
            internal readonly string BatchUrl;
            internal readonly string TimeUrl;

            internal Endpoint(string batchUrl, string timeUrl)
            {
                BatchUrl = batchUrl;
                TimeUrl = timeUrl;
            }
        }
    }
}
