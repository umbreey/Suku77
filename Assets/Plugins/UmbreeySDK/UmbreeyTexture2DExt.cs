using UnityEngine;

public static class UmbreeyTexture2DExt
{
    public static byte[] EncodeToWebP(this Texture2D tex, int quality = -1)
    {
        // 本工程未接入 WebP 原生库；需要时再安装 WebP 插件并恢复实现
        Debug.LogWarning("EncodeToWebP: WebP plugin not available in this project.");
        return null;
    }
    
    public static byte[] BlitEncodeToWebP(this Texture2D tex, int quality = -1, int dstWidth = -1, int dstHeight = -1)
    {
        Debug.LogWarning("BlitEncodeToWebP: WebP plugin not available in this project.");
        return null;
    }

    public static string SaveToImageFile(this Texture2D tex, string filename, bool blitForWebp = false)
    {
        if (tex == null || string.IsNullOrEmpty(filename))
            return null;

        byte[] bytes = null;
        if (filename.EndsWith("png"))
        {
            bytes = tex.EncodeToPNG();
        }
        else if (filename.EndsWith("jpg"))
        {
            bytes = tex.EncodeToJPG();
        }
        else if (filename.EndsWith("webp"))
        {
            if (blitForWebp)
                bytes = tex.BlitEncodeToWebP();
            else
                bytes = tex.EncodeToWebP();
        }
        
        return UmbreeySDK.SaveFile(filename, bytes);
    }
}
