using System;
using UnityEngine;

#if UMBREEY_SDK_WEBGL_DY
using System.Text;
using TTSDK;
using TTSDK.UNBridgeLib.LitJson;
#endif

namespace Umbreey
{
    public partial class PlatformWebGLDY
    {
    #if UMBREEY_SDK_WEBGL_DY   
        private JsonData CreateShareData(UmbreeySDK.ShareParams param)
        {
            var data = new JsonData();
            data["title"] = param.Title;
            data["desc"] = param.Desc;
            
            if (!string.IsNullOrEmpty(param.Query))
            {
                data["query"] = param.Query;
            }
            if (!string.IsNullOrEmpty(param.Template) && param.Template != "undefined")
            {
                data["templateId"] = param.Template;                
            }
            
            // data["channel"] = "picture";
            // {
            //     var pictures = new JsonData();
            //     pictures.Add("Assets/Resources/Icon.png");
            //     
            //     data["extra"] = new JsonData()
            //     {
            //         ["picturePath"] = pictures,
            //         ["contentTitle"] = "本地分享",
            //         ["contentDescription"] = "这是来自本地的分享"
            //     };                
            // }

            return data;
        }
    #endif   
        internal override void Share(Action<UmbreeySDK.StatusCode> callback, UmbreeySDK.ShareParams param)
        {
    #if UMBREEY_SDK_WEBGL_DY     
            var data = CreateShareData(param);
            TT.ShareAppMessage(data, objects =>
            {
                callback?.Invoke(UmbreeySDK.StatusCode.Success);
            }, errMsg =>
            {
                UmbreeySDK.LogError($"TT.ShareAppMessage failure. errMsg:{errMsg}");
                callback?.Invoke(UmbreeySDK.StatusCode.Failure);
            }, () =>
            {
                callback?.Invoke(UmbreeySDK.StatusCode.Cancel);
            });
    #endif
        }
        
        //////////////////////////////////////////////////////////////////////////////////////////////////////////
        /// Below is the code for override webgl platform
        //////////////////////////////////////////////////////////////////////////////////////////////////////////
        public override void OnShareMenu(Action<UmbreeySDK.StatusCode> callback, Func<UmbreeySDK.ShareParams> getShareParam)
        {
    #if UMBREEY_SDK_WEBGL_DY
            TT.OnShareAppMessage((option) =>
            {
                JsonData shareJson = null;
                {
                    var result = getShareParam?.Invoke();
                    if (result.HasValue && !string.IsNullOrEmpty(result.Value.Template))
                    {
                        shareJson = CreateShareData(result.Value);
                    }
                    else
                    {
                        UmbreeySDK.LogError($"TT.OnShareAppMessage getShareParam not valid. hasValue:{result.HasValue} title:{result?.Title} Template:{result?.Template}");
                        callback?.Invoke(UmbreeySDK.StatusCode.Fallback);
                        return null;
                    }
                }

                var param = new TTShare.ShareParam(shareJson, (data) =>
                {
                    StringBuilder ss = new StringBuilder();
                    foreach (var (k, v) in data)
                    {
                        ss.Append($"{k}:{v} ");
                    }

                    UmbreeySDK.Log($"TT.OnShareAppMessage success. {ss}");
                    callback?.Invoke(UmbreeySDK.StatusCode.Success);
                }, errMsg =>
                {
                    UmbreeySDK.LogError($"TT.OnShareAppMessage failure. errorMsg:{errMsg}");
                    callback?.Invoke(UmbreeySDK.StatusCode.Failure);
                }, () =>
                {
                    UmbreeySDK.LogWarning($"TT.OnShareAppMessage cancel.");
                    callback?.Invoke(UmbreeySDK.StatusCode.Cancel);
                });

                return param;
            });
    #endif
        }

        public override void ShowShareMenu(bool showAppMessage, bool showTimeline)
        {
    #if UMBREEY_SDK_WEBGL_DY
            if (showAppMessage)
            {
                TT.ShowShareMenu(() =>
                {
                    UmbreeySDK.Log("TT.ShowShareMenu success.");
                }, errMsg =>
                {
                    UmbreeySDK.LogError($"TT.ShowShareMenu failure. errMsg:{errMsg}");
                });
            }
            else
            {
                TT.HideShareMenu(() =>
                {
                    UmbreeySDK.Log("TT.HideShareMenu success.");
                }, errMsg =>
                {
                    UmbreeySDK.LogError($"TT.HideShareMenu failure. errMsg:{errMsg}");
                });
            }
    #endif           
        }
        
    }
}
