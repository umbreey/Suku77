using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if UMBREEY_SDK_WEBGL_WX
using WeChatWASM;
#endif

namespace Umbreey
{
    public partial class PlatformWebGLWX
    {
        internal override void Share(Action<UmbreeySDK.StatusCode> callback, UmbreeySDK.ShareParams param)
        {
    #if UMBREEY_SDK_WEBGL_WX
                WX.ShareAppMessage(new ShareAppMessageOption()
                {
                    title = param.Title,
                    imageUrlId = param.Template,
                    imageUrl = param.Url,
                    query = param.Query,
                });
                callback?.Invoke(UmbreeySDK.StatusCode.Success);
    #endif     
        }
        
        //////////////////////////////////////////////////////////////////////////////////////////////////////////
        /// Below is the code for override webgl platform
        //////////////////////////////////////////////////////////////////////////////////////////////////////////
        public override void OnShareMenu(Action<UmbreeySDK.StatusCode> callback, Func<UmbreeySDK.ShareParams> getShareParam)
        {
    #if UMBREEY_SDK_WEBGL_WX
            var defaultParam = new WXShareAppMessageParam();
            {
                var result = getShareParam?.Invoke();
                if (result.HasValue && !string.IsNullOrEmpty(result.Value.Title))
                {
                    defaultParam.title = result.Value.Title;
                    defaultParam.imageUrl = result.Value.Url;
                }                
            }
 
            WX.OnShareAppMessage(defaultParam, (shareAppMessageFunc) =>
            {
                var result = getShareParam?.Invoke();
                if (result.HasValue && !string.IsNullOrEmpty(result.Value.Url))
                {
                    var param = new WXShareAppMessageParam {
                        title = result.Value.Title,
                        imageUrl = result.Value.Url
                    };
                    shareAppMessageFunc.Invoke(param);
                    callback?.Invoke(UmbreeySDK.StatusCode.Success);
                }
                else
                {
                    UmbreeySDK.LogError($"WX.OnShareAppMessage getShareParam not valid. hasValue:{result.HasValue} title:{result?.Title} Url:{result?.Url}");
                    callback?.Invoke(UmbreeySDK.StatusCode.Fallback);
                }
            });
                
            WX.OnShareTimeline((a) =>
            {
                var result = getShareParam?.Invoke();
                if (result.HasValue && !string.IsNullOrEmpty(result.Value.Url))
                {
                    var param = new OnShareTimelineListenerResult() {
                        title = result.Value.Title,
                        path = result.Value.Url,
                    };
                    a.Invoke(param);
                    callback?.Invoke(UmbreeySDK.StatusCode.Success);
                }
                else
                {
                    UmbreeySDK.LogError($"WX.OnShareTimeline getShareParam not valid. hasValue:{result.HasValue} title:{result?.Title} Url:{result?.Url}");
                    callback?.Invoke(UmbreeySDK.StatusCode.Fallback);
                }
            });
    #endif
        }

        public override void ShowShareMenu(bool showAppMessage, bool showTimeline)
        {
#if UMBREEY_SDK_WEBGL_WX
            if (showAppMessage || showTimeline)
            {
                List<string> menus = new List<string>();
                if (showAppMessage) menus.Add("shareAppMessage");
                if (showTimeline) menus.Add("shareTimeline");
                
                WX.ShowShareMenu(new ShowShareMenuOption() {
                    menus = menus.ToArray()
                });
            }

            if (!showAppMessage || !showTimeline)
            {
                List<string> menus = new List<string>();
                if (!showAppMessage) menus.Add("shareAppMessage");
                if (!showTimeline) menus.Add("shareTimeline");
                
                WX.HideShareMenu(new HideShareMenuOption() {
                    menus = menus.ToArray()
                });
            }
#endif           
        }
        
        public void CaptureScreen(Action<string> callBack)
        {
#if UMBREEY_SDK_WEBGL_WX
            WXToTempFilePathParam param = new WXToTempFilePathParam();
            param.success += (result) =>
            {
                UmbreeySDK.Log($"WXCanvas.ToTempFilePath success: {result.errMsg} {result.tempFilePath}");
                callBack?.Invoke(result.tempFilePath);
            };
            param.complete += (result) =>
            {
                UmbreeySDK.Log($"WXCanvas.ToTempFilePath complete: {result.errMsg}");
            };
            param.fail += (result) =>
            {
                UmbreeySDK.LogError($"WXCanvas.ToTempFilePath fail: {result.errCode} {result.errMsg}");
            };
            //截图获取本地URL 
            WXCanvas.ToTempFilePath(param);
#endif
        }
        
        public void ShareImage(Action<UmbreeySDK.StatusCode> callback, string path)
        {
    #if UMBREEY_SDK_WEBGL_WX
            ShowShareImageMenuOption option = new ShowShareImageMenuOption();
            option.path = path;
            option.needShowEntrance = true;
            option.success = (data) =>
            {
                callback?.Invoke(UmbreeySDK.StatusCode.Success);
            };
            option.complete = (data) =>
            {
                UmbreeySDK.Log($"WX.ShowShareImageMenu complete. path:{path} {data.errMsg}");
            };
            option.fail = (data) =>
            {
                UmbreeySDK.LogError($"WX.ShowShareImageMenu failure. path:{path} errMsg:{data.errMsg}");
                callback?.Invoke(UmbreeySDK.StatusCode.Failure);
            };
            WX.ShowShareImageMenu(option);
    #endif
        }

    }
}
