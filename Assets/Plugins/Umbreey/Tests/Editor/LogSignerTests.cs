using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Umbreey.Tests
{
    /// <summary>
    /// LogSigner 的 EditMode 测试（票 02 / specs 0002）。
    /// <para>
    /// 核心是协议 §3.4 的测试向量：断言**三个中间值**而不只是最终签名，
    /// 这样对不上的时候能立刻知道是哪一段错了。
    /// </para>
    /// </summary>
    public class LogSignerTests
    {
        // ================= 协议 §3.4 测试向量 =================
        // 这些值直接取自 docs/client-api-v1.md §3.4，不要"顺手修正"。

        private const string AppSecret = "test_secret_123456";
        private const string Timestamp = "1756881000";

        /// <summary>
        /// 签名载荷（payload_raw）。必须是**单行、无缩进、无换行**的 311 字节 UTF-8。
        /// 用逐字串字面量（@""）保留原样；里面的每个引号在源码里写成两个。
        /// </summary>
        private const string PayloadJson =
            @"{""v"":1,""app_id"":""game_a"",""client_id"":""7f3a2b1c-4d5e-6f70-8192-a3b4c5d6e7f8"",""uid"":""10086"",""channel"":""taptap"",""app_version"":""1.2.3"",""platform"":""android"",""device"":""Pixel 8"",""topic"":""client"",""tags"":{""env"":""prod""},""logs"":[{""ts"":1756881000123,""level"":""INFO"",""msg"":""connect gateway"",""fields"":{""gw"":""s1"",""rtt"":""42""}}]}";

        private const int PayloadByteCount = 311;

        private const string ExpectedPayloadSha256 =
            "4005c679d7ffd0ac9ff4d3557961780b2826e051eb889211a9c61f77f5abe4c6";

        private const string ExpectedSignature =
            "4228bcd1b4b20a1ae06a2f6982d705cfa9434bf44944baea25dfaeeb4acfe0a1";

        /// <summary>
        /// 期望的 StringToSign。这里用 <c>(char)0x0A</c> 而不是转义 <c>\n</c> 拼接，
        /// 使断言不依赖"源码里那两个字符有没有写对"。
        /// </summary>
        private static readonly string ExpectedStringToSign =
            Timestamp + (char)0x0A + ExpectedPayloadSha256;

        // 其余常量（本地独立算出，用作回归锚点）
        private const string Sha256OfEmpty =
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        private const string Sha256OfSingleByteA =
            "ca978112ca1bbdcafac231b39a23dc4da786eff8147c4e72b9807785afee48bb";

        // 下面三个非 ASCII 常量一律写成 \uXXXX 转义而不是直接写汉字：
        // 它们是断言的锚点，不能因为源文件被存成 GBK / 加了 BOM 之类的原因而悄悄变值。

        /// <summary>UTF-8 编码的 <c>{"msg":"中文日志测试"}</c>，28 字节。</summary>
        private const string NonAsciiPayloadText = "{\"msg\":\"\u4e2d\u6587\u65e5\u5fd7\u6d4b\u8bd5\"}";

        private const int NonAsciiPayloadUtf8ByteCount = 28;

        private const string Sha256OfNonAsciiPayloadUtf8 =
            "cee6f62c250a6ba7d44871db2233425d5579a8b7e0842069638c26e44ba43c7a";

        /// <summary>同一段文本若被 GBK（本地编码）编码后再 hash 会得到这个值 —— 必须**不**等于它。</summary>
        private const string Sha256OfNonAsciiPayloadGbk =
            "ebb45d9531546b4f7e490775b3811f038df72c06623fbca5bc52859d708ef450";

        /// <summary>非 ASCII 的 appSecret（"密钥_test_123"），用来钉住 key 走的是 UTF-8 字节。</summary>
        private const string NonAsciiSecret = "\u5bc6\u94a5_test_123";

        private const string SignatureWithNonAsciiSecret =
            "26a76910630a2b38b5c072d80c9317eb5887ee6abb685f60f3c0edcf302cd878";

        /// <summary>非 ASCII 的 StringToSign 后半段，用来钉住待签消息也走 UTF-8 字节。</summary>
        private const string NonAsciiDigestText = "\u4e2d\u6587\u65e5\u5fd7\u6d4b\u8bd5";

        private const string SignatureWithNonAsciiDigest =
            "c76d2b6e0b832b7afe4d24fad69ddd1bd1fd79849fac4b93c78ee040c7e982d5";

        private static readonly Regex LowercaseHex64 = new Regex("^[0-9a-f]{64}$");

        private static byte[] VectorPayloadBytes()
        {
            return Encoding.UTF8.GetBytes(PayloadJson);
        }

        // ================= 协议向量：三个中间值 =================

        /// <summary>
        /// 第 0 步：签名载荷本身必须是 311 字节。
        /// 这一条能直接抓出源码里字符串被意外转义、被编辑器插入换行、
        /// 或者被格式化工具折行的问题 —— 后面三个中间值全错的最常见原因。
        /// </summary>
        [Test]
        public void Vector_Payload_Is311Utf8Bytes()
        {
            Assert.That(PayloadJson, Does.Not.Contain("\n"), "签名载荷里不能有换行");
            Assert.That(PayloadJson, Does.Not.Contain("\r"), "签名载荷里不能有回车");
            Assert.That(Encoding.UTF8.GetByteCount(PayloadJson), Is.EqualTo(PayloadByteCount),
                "签名载荷的 UTF-8 字节数与协议 §3.4 不一致，说明字面量被改动或被转义了");
        }

        /// <summary>中间值 1：sha256(payload_raw)。</summary>
        [Test]
        public void Vector_Sha256Hex_MatchesProtocol()
        {
            var actual = LogSigner.Sha256Hex(VectorPayloadBytes());

            Assert.That(actual, Is.EqualTo(ExpectedPayloadSha256),
                "中间值 1 不符：sha256(payload_raw) 错了。先查签名载荷字节，再查是不是算到了 gzip 之后的字节");
        }

        /// <summary>中间值 2：StringToSign，且必须用真实换行符。</summary>
        [Test]
        public void Vector_StringToSign_MatchesProtocol()
        {
            var actual = LogSigner.BuildStringToSign(Timestamp, ExpectedPayloadSha256);

            Assert.That(actual, Is.EqualTo(ExpectedStringToSign), "中间值 2 不符：StringToSign 拼错了");

            // 结构性断言：分隔符必须是**一个真实的 LF**，不是字面量反斜杠 n、不是 CRLF。
            Assert.That(actual.Length, Is.EqualTo(Timestamp.Length + 1 + 64),
                "StringToSign 长度不对，分隔符可能不是单个字符");
            Assert.That(actual[Timestamp.Length], Is.EqualTo((char)0x0A), "分隔符必须是 LF(0x0A)");
            Assert.That(actual, Does.Not.Contain("\\n"), "分隔符写成了字面量反斜杠 n");
            Assert.That(actual, Does.Not.Contain("\r"), "分隔符里混进了 CR，协议要的是单个 LF");
            Assert.That(actual.Split((char)0x0A).Length, Is.EqualTo(2), "StringToSign 里应当恰好有一个换行");
        }

        /// <summary>中间值 3：signature。</summary>
        [Test]
        public void Vector_Signature_MatchesProtocol()
        {
            var actual = LogSigner.Sign(AppSecret, Timestamp, ExpectedPayloadSha256);

            Assert.That(actual, Is.EqualTo(ExpectedSignature),
                "中间值 3 不符：前两个中间值都对的话，问题在 HMAC —— 查 key 是不是 appSecret 的 UTF-8 字节");
        }

        /// <summary>
        /// 端到端跑一遍完整链路（序列化 → sha256 → 签名），三个中间值依次断言。
        /// 与上面三个单点测试重复是故意的：这一条模拟真实调用顺序，
        /// 上面三条在失败时精确定位。
        /// </summary>
        [Test]
        public void Vector_FullChain_AssertsAllThreeIntermediates()
        {
            var payloadRaw = VectorPayloadBytes();
            Assert.That(payloadRaw.Length, Is.EqualTo(PayloadByteCount), "第 0 步：签名载荷字节数");

            var sha = LogSigner.Sha256Hex(payloadRaw);
            Assert.That(sha, Is.EqualTo(ExpectedPayloadSha256), "第 1 步：sha256(payload_raw)");

            var stringToSign = LogSigner.BuildStringToSign(Timestamp, sha);
            Assert.That(stringToSign, Is.EqualTo(ExpectedStringToSign), "第 2 步：StringToSign");

            var signature = LogSigner.Sign(AppSecret, Timestamp, sha);
            Assert.That(signature, Is.EqualTo(ExpectedSignature), "第 3 步：signature");
        }

        /// <summary>签名基于 gzip **之前**的字节 —— 拿压缩后的字节去算一定不等于协议向量。</summary>
        [Test]
        public void Vector_SignatureIsOverUncompressedPayload()
        {
            var payloadRaw = VectorPayloadBytes();
            var gzipped = LogSigner.Gzip(payloadRaw);

            Assert.That(LogSigner.Sha256Hex(gzipped), Is.Not.EqualTo(ExpectedPayloadSha256),
                "如果这条通过了，说明 sha256 对压缩前后不敏感，测试本身写错了");
            Assert.That(LogSigner.Sha256Hex(payloadRaw), Is.EqualTo(ExpectedPayloadSha256));
        }

        // ================= 输出格式 =================

        [Test]
        public void Output_IsLowercaseHex()
        {
            var sha = LogSigner.Sha256Hex(VectorPayloadBytes());
            var signature = LogSigner.Sign(AppSecret, Timestamp, sha);

            Assert.That(LowercaseHex64.IsMatch(sha), Is.True, "sha256 必须是 64 字符小写 hex，实际：" + sha);
            Assert.That(LowercaseHex64.IsMatch(signature), Is.True, "签名必须是 64 字符小写 hex，实际：" + signature);
        }

        /// <summary>
        /// hex 小写化不能受线程 culture 影响。tr-TR 是经典的 ToLower 陷阱区域，
        /// 而上传线程的 culture 不由日志系统控制。
        /// </summary>
        [Test]
        public void Output_IsCultureInvariant()
        {
            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("tr-TR");

                Assert.That(LogSigner.Sha256Hex(VectorPayloadBytes()), Is.EqualTo(ExpectedPayloadSha256));
                Assert.That(LogSigner.Sign(AppSecret, Timestamp, ExpectedPayloadSha256), Is.EqualTo(ExpectedSignature));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        // ================= 编码 =================

        /// <summary>含中文的签名载荷走 UTF-8 字节，不能走本地编码（GBK）。</summary>
        [Test]
        public void Sha256Hex_NonAsciiPayload_UsesUtf8Bytes()
        {
            var utf8 = Encoding.UTF8.GetBytes(NonAsciiPayloadText);

            Assert.That(utf8.Length, Is.EqualTo(NonAsciiPayloadUtf8ByteCount));
            Assert.That(LogSigner.Sha256Hex(utf8), Is.EqualTo(Sha256OfNonAsciiPayloadUtf8));
            Assert.That(LogSigner.Sha256Hex(utf8), Is.Not.EqualTo(Sha256OfNonAsciiPayloadGbk),
                "算出了 GBK 版本的 hash，说明某处走了本地编码");
        }

        /// <summary>HMAC 的 key 是 appSecret 的 UTF-8 字节 —— 用非 ASCII 密钥钉死。</summary>
        [Test]
        public void Sign_HmacKey_IsUtf8BytesOfSecret()
        {
            Assert.That(LogSigner.Sign(NonAsciiSecret, Timestamp, ExpectedPayloadSha256),
                Is.EqualTo(SignatureWithNonAsciiSecret),
                "非 ASCII 密钥的签名不符：key 没有按 UTF-8 取字节");
        }

        /// <summary>待签消息同样按 UTF-8 取字节。</summary>
        [Test]
        public void Sign_Message_IsUtf8BytesOfStringToSign()
        {
            Assert.That(LogSigner.Sign(AppSecret, Timestamp, NonAsciiDigestText),
                Is.EqualTo(SignatureWithNonAsciiDigest),
                "非 ASCII 待签消息的签名不符：StringToSign 没有按 UTF-8 取字节");
        }

        /// <summary>
        /// key 用的是 appSecret 本身，不是它的 hex 编码 ——
        /// 把 hex 当密钥传进去必须得到不同的签名。
        /// </summary>
        [Test]
        public void Sign_HmacKey_IsNotHexOfSecret()
        {
            var hexOfSecret = BitConverter.ToString(Encoding.UTF8.GetBytes(AppSecret)).Replace("-", "").ToLower();
            var signatureWithHexKey = LogSigner.Sign(hexOfSecret, Timestamp, ExpectedPayloadSha256);

            Assert.That(signatureWithHexKey, Is.Not.EqualTo(ExpectedSignature),
                "用 appSecret 的 hex 当 key 竟然也能算出协议向量的签名，不可能");
            Assert.That(LogSigner.Sign(AppSecret, Timestamp, ExpectedPayloadSha256), Is.EqualTo(ExpectedSignature));
        }

        // ================= 边界 =================

        [Test]
        public void Sha256Hex_EmptyPayload_DoesNotThrow()
        {
            Assert.That(LogSigner.Sha256Hex(new byte[0]), Is.EqualTo(Sha256OfEmpty));
        }

        [Test]
        public void Sha256Hex_SingleBytePayload_DoesNotThrow()
        {
            Assert.That(LogSigner.Sha256Hex(new byte[] { (byte)'a' }), Is.EqualTo(Sha256OfSingleByteA));
        }

        [Test]
        public void Sign_EmptyStrings_DoesNotThrow()
        {
            Assert.That(LowercaseHex64.IsMatch(LogSigner.Sign("", "", "")), Is.True);
        }

        [Test]
        public void NullArguments_ThrowInsteadOfSigningGarbage()
        {
            Assert.Throws<ArgumentNullException>(() => LogSigner.Sha256Hex(null));
            Assert.Throws<ArgumentNullException>(() => LogSigner.Gzip(null));
            Assert.Throws<ArgumentNullException>(() => LogSigner.Sign(null, Timestamp, ExpectedPayloadSha256));
            Assert.Throws<ArgumentNullException>(() => LogSigner.Sign(AppSecret, null, ExpectedPayloadSha256));
            Assert.Throws<ArgumentNullException>(() => LogSigner.Sign(AppSecret, Timestamp, null));
        }

        // ================= 无状态 =================

        [Test]
        public void Stateless_SameInputTwice_SameOutput()
        {
            var payloadRaw = VectorPayloadBytes();

            Assert.That(LogSigner.Sha256Hex(payloadRaw), Is.EqualTo(LogSigner.Sha256Hex(payloadRaw)));
            Assert.That(LogSigner.Sign(AppSecret, Timestamp, ExpectedPayloadSha256),
                Is.EqualTo(LogSigner.Sign(AppSecret, Timestamp, ExpectedPayloadSha256)));

            // Gzip 只断言两次的**解压结果**相同，不断言压缩字节逐字节相同：
            // gzip 头里的 MTIME 字段由运行时实现决定，而签名算的是压缩前的 payload_raw，
            // 压缩字节是否可复现对协议没有影响。
            Assert.That(FileUtils.Decompress(LogSigner.Gzip(payloadRaw)),
                Is.EqualTo(FileUtils.Decompress(LogSigner.Gzip(payloadRaw))));
        }

        /// <summary>
        /// 并发调用必须全部算对。LogSigner 会在上传线程上被调用，
        /// 而项目里已有的 FileUtils.s_MD5 正是"缓存一个静态 HashAlgorithm 实例"的写法 ——
        /// 这条测试就是防止那个写法被复制过来。
        /// </summary>
        [Test]
        public void Stateless_ParallelCalls_AllMatchVector()
        {
            const int iterations = 200;
            var payloadRaw = VectorPayloadBytes();
            var shas = new string[iterations];
            var signatures = new string[iterations];

            Parallel.For(0, iterations, i =>
            {
                shas[i] = LogSigner.Sha256Hex(payloadRaw);
                signatures[i] = LogSigner.Sign(AppSecret, Timestamp, shas[i]);
            });

            for (var i = 0; i < iterations; i++)
            {
                Assert.That(shas[i], Is.EqualTo(ExpectedPayloadSha256), "并发第 " + i + " 次 sha256 不符");
                Assert.That(signatures[i], Is.EqualTo(ExpectedSignature), "并发第 " + i + " 次签名不符");
            }
        }

        // ================= 压缩 =================

        [Test]
        public void Gzip_RoundTrip_IsByteForByteIdentical()
        {
            var cases = new[]
            {
                VectorPayloadBytes(),
                new byte[0],
                new byte[] { 0x00 },
                Encoding.UTF8.GetBytes(NonAsciiPayloadText),
                MakeCompressibleBuffer(256 * 1024),
            };

            foreach (var original in cases)
            {
                var restored = FileUtils.Decompress(LogSigner.Gzip(original));

                Assert.That(restored, Is.EqualTo(original),
                    "gzip 往返后字节不一致，长度 " + original.Length);
            }
        }

        [Test]
        public void Gzip_ProducesGzipMagicHeader()
        {
            var compressed = LogSigner.Gzip(VectorPayloadBytes());

            Assert.That(compressed.Length, Is.GreaterThan(2));
            Assert.That(compressed[0], Is.EqualTo(0x1f), "gzip 魔数第一字节应为 0x1f");
            Assert.That(compressed[1], Is.EqualTo(0x8b), "gzip 魔数第二字节应为 0x8b");
        }

        private static byte[] MakeCompressibleBuffer(int size)
        {
            var buffer = new byte[size];
            for (var i = 0; i < size; i++)
            {
                buffer[i] = (byte)('a' + (i % 26));
            }
            return buffer;
        }

        // ================= 契约 C4 =================

        /// <summary>
        /// 契约 C4：三个方法必须是 static，且类本身是静态类（无实例状态）。
        /// 用反射钉住，避免以后有人给它加实例字段或改成单例。
        /// </summary>
        [Test]
        public void Contract_ThreeMethodsAreStaticAndTypeHasNoInstanceState()
        {
            var type = typeof(LogSigner);

            Assert.That(type.IsAbstract && type.IsSealed, Is.True, "LogSigner 必须是静态类");

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Static | BindingFlags.Instance;

            foreach (var name in new[] { "Sha256Hex", "Sign", "Gzip" })
            {
                var method = type.GetMethod(name, flags);
                Assert.That(method, Is.Not.Null, "契约 C4 要求的方法缺失：" + name);
                Assert.That(method.IsStatic, Is.True, name + " 必须是 static");
            }

            // const 是编译期字面量，不构成状态，所以排除掉；其余任何字段都不允许。
            var stateFields = Array.FindAll(type.GetFields(flags), f => !f.IsLiteral);
            Assert.That(stateFields.Length, Is.EqualTo(0),
                "LogSigner 不应持有任何非常量字段（含缓存的 HashAlgorithm 实例）—— 它要在上传线程上被调用");
        }
    }
}
