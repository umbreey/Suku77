using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace Umbreey
{
    public class FileUtils 
    {
        public static string GetMD5Hash(string path)
        {
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            return GetMD5Hash(File.ReadAllBytes(path));
        }

        private static readonly MD5 s_MD5 = new MD5CryptoServiceProvider();
        public static string GetMD5Hash(byte[] buffer)
        {
            if (buffer == null)
            {
                return string.Empty;
            }

            return BitConverter.ToString(s_MD5.ComputeHash(buffer)).Replace("-", "").ToLower();
        }

        public static long GetFileSize(string file)
        {
            if(File.Exists(file))
                return new FileInfo(file).Length;
            return 0;
        }
        
        private static readonly string[] s_FormatSize = { "B", "KB", "MB", "GB", "TB" };
        public static string GetFileFormatSize(long bytes)
        {
            int order = 0;
            double dou = bytes;
            while (dou >= 1024f && order < s_FormatSize.Length - 1)
            {
                order++;
                dou/= 1024f;
            }
            return String.Format("{0:0.##} {1}", dou, s_FormatSize[order]);
        }

#region Load
        public static string GetStreamingAssetsPath()
        {
    #if UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX || UNITY_IOS
            return $"file://{Application.streamingAssetsPath}";
    #else
            return $"{Application.streamingAssetsPath}";    
    #endif
        }
        
        public static string GetDownloadPath()
        {
            return Download.UMBREEY_DOWNLOAD_PATH;    
        }
        
        public static string LoadStreamingText(string name, bool trying = false)
        {
            string path = $"{GetStreamingAssetsPath()}/{name}";
            return LoadFromPath(path, trying).text;
        }
        
        public static byte[] LoadStreamingBytes(string name, bool trying = false)
        {
            string path = $"{GetStreamingAssetsPath()}/{name}";
            return LoadFromPath(path, trying).data;
        }
        
        public static void LoadStreamingTextAsync(string name, Action<string> callback)
        {
            string path = $"{GetStreamingAssetsPath()}/{name}";
            Umbreey.Inst.StartCoroutine(LoadFromPathAsync(path, (h) =>
            {
                callback?.Invoke(h.text);
            }));
        }
        
        public static void LoadStreamingBytesAsync(string name, Action<byte[]> callback)
        {
            string path = $"{GetStreamingAssetsPath()}/{name}";
            Umbreey.Inst.StartCoroutine(LoadFromPathAsync(path, (h) =>
            {
                callback?.Invoke(h.data);
            }));
        }
        
        //保存Txt到Download目录
        public static void SaveTextToDownload(string name, string text)
        {
            File.WriteAllText($"{GetDownloadPath()}/{name}", text);
        }

        public static string LoadDownloadText(string name)
        {
            string path = $"{GetDownloadPath()}/{name}"; 
            if(File.Exists(path))
                return File.ReadAllText(path);
            return null;
        }
        
        public static void DeleteDownloadFile(string name)
        {
            string path = $"{GetDownloadPath()}/{name}"; 
            if(File.Exists(path))
            {
                File.Delete(path);
            }
        }

        public static byte[] LoadDownloadBytes(string name)
        {
            return File.ReadAllBytes($"{GetDownloadPath()}/{name}");
        }
        
        private static DownloadHandler LoadFromPath(string path, bool trying)
        {
        #if UNITY_WEBGL && !UNITY_EDITOR
            Log.E($"sync load streaming streaming asset may cause stuck on webgl platform: {path}");
        #endif

            UnityWebRequest request = UnityWebRequest.Get(path);
            request.SendWebRequest();
                
            while (!request.isDone)
            {
                if (request.result is UnityWebRequest.Result.ConnectionError or 
                    UnityWebRequest.Result.ProtocolError or 
                    UnityWebRequest.Result.DataProcessingError)
                {
                    var msg = $"sync load failed. path:{path} code:{request.error} error:{request.responseCode}";
                    if (trying) Log.I(msg);
                    else Log.E(msg);
                    
                    break;
                }
            }
            
            return request.downloadHandler;                
        }
        
        private static IEnumerator LoadFromPathAsync(string path, Action<DownloadHandler> handler)
        {
            using UnityWebRequest webRequest = UnityWebRequest.Get(path);
            yield return webRequest.SendWebRequest();

            if (webRequest.result == UnityWebRequest.Result.Success)
            {
                handler?.Invoke(webRequest.downloadHandler);
            }
            else
            {
                Log.E($"{path} async load failed:");
            }
        }
#endregion    
        
#region Compression
        public static byte[] Compress(byte[] bytes)
        {
            using (var memoryStream = new MemoryStream())
            {
                using (var gzipStream = new GZipStream(memoryStream, CompressionMode.Compress))
                {
                    gzipStream.Write(bytes, 0, bytes.Length);
                }
                return memoryStream.ToArray();
            }
        }
        public static string Compress(string str)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(str);
            var compressBytes = Compress(bytes);
            return System.Convert.ToBase64String(compressBytes);
        }
        
        public static byte[] Decompress(byte[] bytes)
        {
            using (var memoryStream = new MemoryStream(bytes))
            {
                using (var outputStream = new MemoryStream())
                {
                    using (var decompressStream = new GZipStream(memoryStream, CompressionMode.Decompress))
                    {
                        decompressStream.CopyTo(outputStream);
                    }
                    return outputStream.ToArray();
                }
            }
        }
        
        public static string Decompress(string str)
        {
            var bytes = System.Convert.FromBase64String(str);
            var decompressBytes = Decompress(bytes);
            return System.Text.Encoding.UTF8.GetString(decompressBytes);
        }
        
        public static void ZipFiles(string outputZipFilePath, params string[] inputFilePaths)
        {
            using (var fileStream = new FileStream(outputZipFilePath, FileMode.Create))
            {
                using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, true))
                {
                    foreach (var filePath in inputFilePaths)
                    {
                        archive.CreateEntryFromFile(filePath, Path.GetFileName(filePath));
                    }
                }
            }
        }
        
        public static byte[] ZipFileInMemory(string path)
        {
            using (var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var streamReader = new StreamReader(fileStream))
            {
                var txt = streamReader.ReadToEnd();
                using (var outStream = new MemoryStream())
                {
                    using (var archive = new ZipArchive(outStream, ZipArchiveMode.Create, true))
                    {
                        var fileInArchive = archive.CreateEntry(Path.GetFileName(path));
                        using (var entryStream = fileInArchive.Open())
                        using (var fileToCompressStream = new MemoryStream(Encoding.UTF8.GetBytes(txt)))
                        {
                            fileToCompressStream.CopyTo(entryStream);
                        }
                    }
                    return outStream.ToArray();
                }
            }
        }
#endregion

#region Encrypt
    public static byte[] EncryptBytes(byte[] bytes)
    {
        bytes[0] = (byte)~bytes[0];
        return FileUtils.Compress(bytes);
    }
        
    public static byte[] DecryptBytes(byte[] bytes)
    {
        var decode= FileUtils.Decompress(bytes);
        decode[0] = (byte)~decode[0];
        return decode;
    }
#endregion
        
    }
}
