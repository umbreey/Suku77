using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Umbreey.Editor
{
    public static class PathUtils
    {
        public static void RecreateDirectory(string dir)
        {
            DeleteDirectory(dir);
            Directory.CreateDirectory(dir);
        }
        
        public static void DeleteDirectory(string dir)
        {
            if (!Directory.Exists(dir))
                return;
            
            try
            {
                Directory.Delete(dir, true);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"directory can't delete: {dir} {e}");
            }
        }
        
        public static void DeleteFile(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        public static void ClearFiles(string dir, string[] exts)
        {
            if (!Directory.Exists(dir))
                return;

            foreach (var ext in exts)
            {
                var files = Directory.GetFiles(dir, $"*{ext}", SearchOption.TopDirectoryOnly);
                foreach (var file in files)
                {
                    File.Delete(file);
                }
            }
        }
        
        public static void ClearDirectory(string dir, string[] ignoreExts = null, string[] ignoreNames = null)
        {
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(file);
                    var ext = Path.GetExtension(file);
                
                    if (ignoreNames != null && ignoreNames.Contains(name))
                        continue;
                    if (ignoreExts != null && ignoreExts.Contains(ext))
                        continue;
                    
                    File.Delete(file);
                }
            }
            else
            {
                Directory.CreateDirectory(dir);
            }
        }
        
        public static void CopyDirectory(string srcDir, string dstDir, string[] ignoreExts = null, string[] ignoreNames = null, bool isEncrypt = false)
        {
            // directories
            if (!Directory.Exists(srcDir))
                return;
            
            if (!Directory.Exists(dstDir))
                Directory.CreateDirectory(dstDir);
            
            // files
            foreach (var srcFilePath in Directory.GetFiles(srcDir, "*.*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(srcFilePath);
                var ext = Path.GetExtension(srcFilePath);
                
                if (ignoreNames != null && ignoreNames.Contains(name))
                    continue;
                if (ignoreExts != null && ignoreExts.Contains(ext))
                    continue;
                
                var dstFilePath = srcFilePath.Replace(srcDir, dstDir);
                var dstFileDir = Path.GetDirectoryName(dstFilePath);
                if (!Directory.Exists(dstFileDir))
                    Directory.CreateDirectory(dstFileDir);

                if (isEncrypt)
                {
                    FileCopyAndEncrypt(srcFilePath, dstFilePath);
                }
                else
                {
                    File.Copy(srcFilePath, dstFilePath, true);
                }
            }
        }

        private static void FileCopyAndEncrypt(string srcPath, string dstPath)
        {
            if (File.Exists(dstPath))
            {
                File.Delete(dstPath);
            }
            
            using var input = new FileStream(srcPath, FileMode.Open);
            using var output = new FileStream(dstPath, FileMode.OpenOrCreate);
            output.WriteByte(255);
            input.CopyTo(output);
        }
    }   
}
