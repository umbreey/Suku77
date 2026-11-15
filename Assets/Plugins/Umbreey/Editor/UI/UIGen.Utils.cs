using System.Security.Cryptography;
using System.Text;
using UnityEditor;

namespace Umbreey.Editor
{
    public partial class UIGen
    {
        [MenuItem("Umbreey/UIGen")]
        public static void GenUI()
        {
            Config config = new Config()
            {
                BaseUI = "BaseUI",
                BaseUISub = "BaseUISub",
                BasePath = "Assets/HotUpdate/UI/Base",
                SrcPrefabPath = "Assets/AssetBundles/UI/Prefab",
                DstPrefabPath = "Assets/AssetBundles/UI/Prefab/Gen"
            };
        
            Gen(config);
        }

        #region 譁・悽螟・炊蟾･蜈ｷ
        
        /// <summary>
        /// 隶｡邂玲枚譛ｬ逧・ｨｳ螳壼嶋蟶悟ｼ・井ｽｿ逕ｨSHA1邂玲ｳ包ｼ・
        /// </summary>
        /// <param name="text">隕∬ｮ｡邂怜嶋蟶檎噪譁・悽</param>
        /// <returns>16蟄礼ｬｦ逧・香蜈ｭ霑帛宛蜩亥ｸ悟ｭ礼ｬｦ荳ｲ</returns>
        public static string ComputeStableHash(string text)
        {
            using (var sha1 = SHA1.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                var hashBytes = sha1.ComputeHash(bytes);
                var sb = new StringBuilder();
                for (int i = 0; i < 8 && i < hashBytes.Length; i++)
                {
                    sb.Append(hashBytes[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }
        
        /// <summary>
        /// 譽豬区枚譛ｬ譏ｯ蜷ｦ蛹・性荳ｭ譁・ｭ礼ｬｦ
        /// </summary>
        /// <param name="text">隕∵｣豬狗噪譁・悽</param>
        /// <returns>螯よ棡蛹・性荳ｭ譁・ｿ泌屓true・悟凄蛻呵ｿ泌屓false</returns>
        public static bool ContainsChinese(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            foreach (char c in text)
            {
                if (c >= 0x4e00 && c <= 0x9fbb)
                    return true;
            }
            return false;
        }
        
        #endregion
    }
}