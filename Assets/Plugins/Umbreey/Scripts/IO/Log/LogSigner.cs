using System;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

// LogSigner 是 internal（契约 C4），而 EditMode 测试在独立程序集里，
// 因此需要开放 internal 可见性。程序集级特性可以放在本程序集的任意源文件中，
// 这里放在 LogSigner.cs 是为了不去改 Umbreey 程序集里不属于本票（S2）的文件。
// 注意：后续若有别的票新增 AssemblyInfo.cs 并重复声明同一个 InternalsVisibleTo，会编译报错。
[assembly: InternalsVisibleTo("Umbreey.Tests.Editor")]

namespace Umbreey
{
    /// <summary>
    /// 批次上传的签名与压缩（协议 §3.3 / §4.1，设计见 specs/0002）。
    /// <para>
    /// 三个方法全部是 static 无状态纯函数，且不触碰任何 Unity API，
    /// 因此可以在上传线程上安全调用（全局硬约束 1）。
    /// </para>
    /// <para>
    /// 固定顺序：序列化 → <see cref="Sha256Hex"/> → <see cref="Sign"/> → <see cref="Gzip"/> → 发送。
    /// 签名基于 gzip **之前** 的签名载荷（payload_raw），这是协议里最容易搞错的一点。
    /// </para>
    /// <para>
    /// appSecret 只经由参数传入，本类不持有、不缓存、不打印（全局硬约束 4、8）。
    /// </para>
    /// </summary>
    internal static class LogSigner
    {
        /// <summary>
        /// StringToSign 的分隔符。协议 §3.3 要求是**真实换行符**，不是字面量反斜杠 n。
        /// </summary>
        private const char StringToSignSeparator = '\n';

        /// <summary>
        /// 计算签名载荷（payload_raw，gzip 之前的原始 JSON 字节）的 sha256，
        /// 返回 64 字符的**小写**十六进制字符串。
        /// </summary>
        /// <param name="payloadRaw">签名载荷字节。允许空数组；不允许 null。</param>
        internal static string Sha256Hex(byte[] payloadRaw)
        {
            if (payloadRaw == null)
            {
                // 不走 FileUtils.GetMD5Hash 那种"null 返回空串"的兜底：
                // 签名场景下静默返回空串会算出一个"看起来合法但一定错"的签名，
                // 服务端只回 code=2，没有任何其他线索。宁可就地炸。
                throw new ArgumentNullException(nameof(payloadRaw));
            }

            // 两点都是有意为之：
            //
            // 1. 用 new SHA256Managed() 而不是 SHA256.Create()。
            //    SHA256.Create() 内部走 CryptoConfig 的**按名字反射**去找实现类，
            //    IL2CPP 的托管代码裁剪看不见这条引用，可能把实现类裁掉导致真机崩。
            //    直接 new 一个具体类型才有静态引用，链接器就不会动它。
            //    （项目里 FileUtils 用的也是 new MD5CryptoServiceProvider() 这种直接实例化。）
            //
            // 2. 每次新建实例，不像 FileUtils.s_MD5 那样缓存静态实例 ——
            //    本类会被上传线程调用，而 HashAlgorithm 实例不是线程安全的。
            using (var sha256 = new SHA256Managed())
            {
                return ToLowerHex(sha256.ComputeHash(payloadRaw));
            }
        }

        /// <summary>
        /// 计算 X-Signature：<c>hex(HMAC_SHA256(appSecret, StringToSign))</c>，
        /// 返回 64 字符的**小写**十六进制字符串。
        /// </summary>
        /// <param name="appSecret">应用密钥。HMAC 的 key 取它的 **UTF-8 字节**，不是它的 hex 编码。</param>
        /// <param name="unixSecondsText">秒级时间戳的字符串形式，必须与 X-Timestamp 请求头**同一个变量**。</param>
        /// <param name="payloadSha256Hex"><see cref="Sha256Hex"/> 的返回值。</param>
        internal static string Sign(string appSecret, string unixSecondsText, string payloadSha256Hex)
        {
            if (appSecret == null)
            {
                throw new ArgumentNullException(nameof(appSecret));
            }
            if (unixSecondsText == null)
            {
                throw new ArgumentNullException(nameof(unixSecondsText));
            }
            if (payloadSha256Hex == null)
            {
                throw new ArgumentNullException(nameof(payloadSha256Hex));
            }

            var stringToSign = BuildStringToSign(unixSecondsText, payloadSha256Hex);

            // key 是 appSecret 的 UTF-8 字节。这里不做任何 hex/base64 变换。
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret)))
            {
                return ToLowerHex(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
            }
        }

        /// <summary>
        /// gzip 压缩签名载荷。**无条件压缩** —— 阈值判断（GzipMinBytes 以下不压缩）是调用方的职责。
        /// </summary>
        internal static byte[] Gzip(byte[] payloadRaw)
        {
            if (payloadRaw == null)
            {
                throw new ArgumentNullException(nameof(payloadRaw));
            }

            // 复用项目已有的 GZipStream 实现，不新写一份。
            // FileUtils.Compress 与 FileUtils 的静态构造都不碰 Unity API，后台线程调用安全。
            return FileUtils.Compress(payloadRaw);
        }

        /// <summary>
        /// 拼出协议 §3.3 的 StringToSign：<c>timestamp + "\n" + hex(sha256(payload_raw))</c>。
        /// <para>
        /// 这不是契约 C4 的一部分，S4 不应依赖它 —— 它存在只是为了让 EditMode 测试能断言
        /// **中间值本身**（票 02 要求逐字符比对三个中间值，而不只是最终签名）。
        /// </para>
        /// </summary>
        internal static string BuildStringToSign(string unixSecondsText, string payloadSha256Hex)
        {
            return unixSecondsText + StringToSignSeparator + payloadSha256Hex;
        }

        /// <summary>
        /// 沿用项目风格（见 FileUtils.GetMD5Hash）的小写 hex 转换。
        /// ToLower() 在这里与 culture 无关：BitConverter 产出的十六进制字母只有 A-F，
        /// 不含土耳其语 I/i 那种会被特殊折叠的字符。
        /// </summary>
        private static string ToLowerHex(byte[] hash)
        {
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }
    }
}
