using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

#if UMBREEY_SDK_WEBGL_WX
using System.Text;
using WeChatWASM;
#endif

namespace Umbreey
{
    public partial class PlatformWebGLWX : PlatformWebGL
    {
        internal override void Init(Action<UmbreeySDK.StatusCode> callback, params string[] param)
        {
    #if UMBREEY_SDK_WEBGL_WX
			EnableRealtimeLog(true);
			
            // WX.PreloadConcurrent(10); 待验证能减少启动时间了再打开
            WX.InitSDK(code =>
            {
                if (code == 200)
                {
                    callback?.Invoke(UmbreeySDK.StatusCode.Success);
                }
                else
                {
                    UmbreeySDK.LogError($"WX.InitSDK failed: {code}");
                    callback?.Invoke(UmbreeySDK.StatusCode.Failure);
                }
            });
    #endif
        }

        internal override void Login(Action<UmbreeySDK.StatusCode, UmbreeySDK.LoginData> callback, params string[] param)
        {
    #if UMBREEY_SDK_WEBGL_WX
            LoginOption option = new LoginOption()
            {
                complete = data => UmbreeySDK.Log($"WX.Login: {data.errMsg}"),
                success = data => callback?.Invoke(UmbreeySDK.StatusCode.Success, new UmbreeySDK.LoginData()
                {
                    Token = data.code
                }),
                fail = _ => callback?.Invoke(UmbreeySDK.StatusCode.Failure, new UmbreeySDK.LoginData())
            };
            WX.Login(option);   
    #endif
        }

        internal override void Logout(Action<UmbreeySDK.StatusCode> callback, params string[] param)
        {
    #if UMBREEY_SDK_WEBGL_WX
   
    #endif           
        }

        internal override bool SupportsLogout()
        {
    #if UMBREEY_SDK_WEBGL_WX
    #endif    
            return true;
        }
        
        internal override void Pay(Action<UmbreeySDK.StatusCode> callback, params string[] param)
        {
    #if UMBREEY_SDK_WEBGL_WX
   
    #endif           
        }
        internal override void Pay2(Action<UmbreeySDK.StatusCode> callback, params string[] param)
        {
    #if UMBREEY_SDK_WEBGL_WX
   
    #endif              
        }
        
        internal override string GetCustomData(params string[] param)
        {
    #if UMBREEY_SDK_WEBGL_WX
   
    #endif    
            return null;
        }

        internal override string SetCustomData(params string[] param)
        {
    #if UMBREEY_SDK_WEBGL_WX
   
    #endif    
            return null;
        }
        
#if UMBREEY_SDK_WEBGL_WX
        Dictionary<Action<bool>, Action<OnShowListenerResult>> _onShowCallback = new Dictionary<Action<bool>, Action<OnShowListenerResult>>();
        Dictionary<Action<bool>, Action<GeneralCallbackResult>> _onHideCallback = new Dictionary<Action<bool>, Action<GeneralCallbackResult>>();
#endif
        
        internal override void OnGamePause(Action<bool> callback)
        {
    #if UMBREEY_SDK_WEBGL_WX
            OffGamePause(callback);
            
            _onHideCallback.Add(callback, (result) =>
            {
                callback?.Invoke(true);
            });
            WX.OnHide(_onHideCallback[callback]);
            
            _onShowCallback.Add(callback, (result) =>
            {
                callback?.Invoke(false);
            });
            WX.OnShow(_onShowCallback[callback]);
    #endif               
        }

        internal override void OffGamePause(Action<bool> callback)
        {
    #if UMBREEY_SDK_WEBGL_WX
            if(_onShowCallback.ContainsKey(callback))
            {
                WX.OffShow(_onShowCallback[callback]);
                _onShowCallback.Remove(callback);
            }
            
            if (_onHideCallback.ContainsKey(callback))
            {
                WX.OffHide(_onHideCallback[callback]);
                _onHideCallback.Remove(callback);
            }
    #endif              
        }
        
        internal override void QuitGame()
        {
    #if UMBREEY_SDK_WEBGL_WX
            WX.ExitMiniProgram(new ExitMiniProgramOption()
            {
                success = (result) =>
                {
                    UmbreeySDK.Log("WX.ExitMiniProgram success: " + result.errMsg);
                },
                fail = (result) =>
                {
                    UmbreeySDK.LogError("WX.ExitMiniProgram failed: " + result.errMsg);
                },
                complete = (result) =>
                {
                    UmbreeySDK.Log("WX.ExitMiniProgram complete: " + result.errMsg);
                }
            });
    #endif
        }
        
        internal override long GetAvailableFreeSpace()
        {
            return long.MaxValue;
        }

        internal override Rect GetScreenSafeArea()
        {
    #if UMBREEY_SDK_WEBGL_WX
            var area = WX.GetSystemInfoSync().safeArea;
            Rect rect = new Rect((float)area.left, (float)area.bottom, (float)area.width, (float)area.height);
            return rect;
    #else
            return base.GetScreenSafeArea();
    #endif    
        }
        
        internal override void OpenUrl(string url)
        {
    #if UMBREEY_SDK_WEBGL_WX
   
    #endif                
        }

        internal override void Vibrate()
        {
    #if UMBREEY_SDK_WEBGL_WX
            WX.VibrateShort(new VibrateShortOption()
            {
                type = "light"
            });   
    #endif    
        }


        internal override string SaveFile(string filename, byte[] data)
        {
    #if UMBREEY_SDK_WEBGL_WX
            var path = $"{WX.env.USER_DATA_PATH}/{filename}";
            try
            {
                var fileSystemManager = WX.GetFileSystemManager();
                fileSystemManager.WriteFileSync(path, data, "utf8");
                return path;
            }
            catch (Exception e)
            {
                UmbreeySDK.LogError($"saving {path}, errMsg:{e.Message}");
            }
    #endif
            return null;
        }

        internal override void DeleteFile(string filename)
        {
    #if UMBREEY_SDK_WEBGL_WX
            var path = $"{WX.env.USER_DATA_PATH}/{filename}";
            try
            {
                var fileSystemManager = WX.GetFileSystemManager();
                fileSystemManager.RemoveSavedFile(new RemoveSavedFileOption()
                {
                    filePath = path,
                    success = (result) =>
                    {
                        UmbreeySDK.Log($"WX.RemoveSavedFile success. file:{filename}");
                    },
                    fail = (result) =>
                    {
                        UmbreeySDK.LogError($"WX.RemoveSavedFile failed. file:{filename} errMsg: {result.errMsg}");
                    },
                    complete = (result) =>
                    {
                        UmbreeySDK.Log($"WX.RemoveSavedFile complete. file:{filename} {result.errMsg}");
                    }
                });
            }
            catch (Exception e)
            {
                UmbreeySDK.LogError($"deleting: {path}, errMsg: {e.Message}");
            }
    #endif
        }

        internal override void SaveImageToPhotoAlbum(Action<UmbreeySDK.StatusCode> callback, Texture2D texture, string filename, string album)
        {
    #if UMBREEY_SDK_WEBGL_WX
            var filepath = texture.SaveToImageFile(filename);
            if (string.IsNullOrEmpty(filepath))
            {
                UmbreeySDK.LogError($"save texture to image file failed. texture:{texture.name} filename:{filename}");
                callback?.Invoke(UmbreeySDK.StatusCode.Failure);
                return;
            }
            
            var option = new SaveImageToPhotosAlbumOption();
            option.filePath = filepath;
            option.complete = res =>
            {
                UmbreeySDK.Log($"WX.SaveImageToPhotosAlbum complete: {res.errMsg}");
                UmbreeySDK.DeleteFile(filename); // 删除临时文件
            };
            option.success = res =>
            {
                UmbreeySDK.Log("WX.SaveImageToPhotosAlbum success");
                callback?.Invoke(UmbreeySDK.StatusCode.Success);
            };
            option.fail = res =>
            {
                UmbreeySDK.LogError($"WX.SaveImageToPhotosAlbum failed: {res.errMsg}");
                callback?.Invoke(UmbreeySDK.StatusCode.Failure);
            };
            WX.SaveImageToPhotosAlbum(option);
    #endif
        }
        
        //////////////////////////////////////////////////////////////////////////////////////////////////////////
        /// Below is the code for override webgl platform
        //////////////////////////////////////////////////////////////////////////////////////////////////////////
        public override LaunchOptions GetLaunchOptions()
        {
    #if UMBREEY_SDK_WEBGL_WX
            var options = WX.GetLaunchOptionsSync();
            return new LaunchOptions()
            {                
                scene = options.scene.ToString(),
                query = options.query,
            };
    #endif
            return null;
        }
        
        public override void SetPreferredFramesPerSecond(int fps)
        {
    #if UMBREEY_SDK_WEBGL_WX        
             WX.SetPreferredFramesPerSecond(fps);
    #endif           
        }
		
        public override float GetDevicePixelRatio()
        {
    #if UMBREEY_SDK_WEBGL_WX        
            var systemInfo = WX.GetSystemInfoSync();
            return (float)systemInfo.pixelRatio;
    #endif
            return 1.0f;
        }
        
        public override void SetDevicePixelRatio(float dpr)
        {
    #if UMBREEY_SDK_WEBGL_WX
            WX.SetDevicePixelRatio(dpr);
    #endif
        }
        
        
        //////////////////////////////////////////////////////////////////////////////////////////////////////////
        /// Below is the code for weixin platform only
        //////////////////////////////////////////////////////////////////////////////////////////////////////////
#if UMBREEY_SDK_WEBGL_WX
        private WXUserInfoButton _wxUserInfoButton;
#endif 
        
        public override void CreateUserInfoButton(Action<UmbreeySDK.StatusCode, UserData> callback, UnityEngine.UI.Button button, Camera uiCamera = null)
        {
#if UMBREEY_SDK_WEBGL_WX
            void DestroyUserInfoButton()
            {
                if (_wxUserInfoButton != null) {
                    _wxUserInfoButton.Destroy();
                    _wxUserInfoButton = null;
                }
            }
            
            RectInt GetButtonScreenArea(UnityEngine.UI.Button btn, Camera cam)
            {
                Vector3[] corners = new Vector3[4];
                var rectTransform = btn.GetComponent<RectTransform>();
                rectTransform.GetWorldCorners(corners); // 获取RectTransform的四个角坐标
                
                Vector3 screenPos = corners[1]; // 取左上角（索引1）作为基准点
                if (cam != null) // 非Screen Space - Overlay时，需要转换
                {
                    screenPos = cam.WorldToScreenPoint(screenPos);
                }
                screenPos.y = Screen.height - screenPos.y; // Unity屏幕坐标系原点在左下角，转换为左上角坐标系
    
                var rect = rectTransform.rect;
                return new RectInt((int)screenPos.x, (int)screenPos.y, (int)rect.width, (int)rect.height);
            }
            
            // 1. 计算按钮在屏幕上的区域
            RectInt screenArea;
            
            bool useButton = button != null;
            if (useButton)
            {
                screenArea = GetButtonScreenArea(button, uiCamera);
            }
            else
            {
                screenArea = new RectInt(0, 0, Screen.width, Screen.height); // 全屏区域
            }

            // 2. 创建用户信息按钮
            DestroyUserInfoButton();
            UmbreeySDK.Log($"WX.CreateUserInfoButton area: {screenArea}");
            
            _wxUserInfoButton = WX.CreateUserInfoButton(screenArea.x, screenArea.y, screenArea.width, screenArea.height, "en", false);
            _wxUserInfoButton.OnTap(data =>
            {
                DestroyUserInfoButton();
                if (useButton && button == null)
                {
                    return; // 按钮已被销毁，忽略回调
                }
                UmbreeySDK.Log($"WX.CreateUserInfoButton OnTap. code:{data.errCode} data:{data.userInfoRaw} error:{data.errMsg}");
                
                if (data.errCode == 0)
                {
                    var userData = new UserData()
                    {
                        NickName = data.userInfo.nickName,
                        AvatarUrl = data.userInfo.avatarUrl,
                        Gender = data.userInfo.gender
                    };
                    callback?.Invoke(UmbreeySDK.StatusCode.Success, userData);
                }
                else if (data.errCode == 1)
                {
                    callback?.Invoke(UmbreeySDK.StatusCode.Cancel, new UserData());
                }
                else
                {
                    callback?.Invoke(UmbreeySDK.StatusCode.Failure, new UserData());
                }
            });
#endif
        }

        public override void GetUserInfo(Action<UmbreeySDK.StatusCode, UserData> callback, bool openSetting = false)
        {
    #if UMBREEY_SDK_WEBGL_WX
            WX.GetSetting(new GetSettingOption()
            {
                complete = d => UmbreeySDK.Log($"WX.GetSetting: {d.errMsg}"),
                success = d =>
                {
                    const string SCOPE_USER_INFO = "scope.userInfo";
                    
                    // 用户曾经拒绝授权
                    if (d.authSetting.TryGetValue(SCOPE_USER_INFO, out var authorized) && !authorized)
                    {
                        if (openSetting) // 打开设置界面引导开启授权
                        {
                            WX.OpenSetting( new OpenSettingOption()
                            {
                                complete = o => UmbreeySDK.Log($"WX.OpenSetting: {o.errMsg}"),
                                success = o =>
                                {
                                    UmbreeySDK.Log($"WX.OpenSetting success. userInfo:{o.authSetting[SCOPE_USER_INFO]}");
                                    callback?.Invoke(UmbreeySDK.StatusCode.Cancel, new UserData());
                                },
                                fail = o =>
                                {
                                    UmbreeySDK.LogError($"TT.OpenSetting errMsg:{o.errMsg}");
                                    callback?.Invoke(UmbreeySDK.StatusCode.Failure, new UserData());
                                }
                            });                            
                        }
                        else
                        {
                            callback?.Invoke(UmbreeySDK.StatusCode.Cancel, new UserData());
                        }
                    }
                    else
                    {
                        // 用户已同意授权，直接获取用户信息
                        if (authorized) 
                        {
                            WX.GetUserInfo(new GetUserInfoOption()
                            {
                                complete = data => UmbreeySDK.Log($"WX.GetUserInfo: {data.errMsg}"),
                                success = data =>
                                {
                                    UmbreeySDK.Log($"WX.GetUserInfo rawdata: {data.rawData} error:{data.errMsg}");
                                    var userData = new UserData
                                    {
                                        NickName = data.userInfo.nickName,
                                        AvatarUrl = data.userInfo.avatarUrl,
                                        Gender = (int)data.userInfo.gender
                                    };
                                    callback?.Invoke(UmbreeySDK.StatusCode.Success, userData);
                                },
                                fail = _ => callback?.Invoke(UmbreeySDK.StatusCode.Failure, new UserData())
                            });
                        }
                        else // 用户未授权过，创建按钮让用户主动授权
                        {
                            CreateUserInfoButton(callback, null);
                        }
                    }
                }, 
                fail = d => callback?.Invoke(UmbreeySDK.StatusCode.Failure, new UserData())
            });
    #endif
        }
        
        public void GetFont(string fallbackUrl, Action<Font> callback)
        {
    #if UMBREEY_SDK_WEBGL_WX        
            WX.GetWXFont(fallbackUrl, callback);
    #endif           
        }
        
        public struct ShowKeyboardParam
        {
            public bool confirmHold;
            public string DefaultValue;
            public double MaxLength;
            public string ConfirmType;
            public Action<string> InputCallback;
            public Action ConfirmCallback;
            public Action CompleteCallback;
            public Action ShowSuccessCallBack;
        }
        
    #if UMBREEY_SDK_WEBGL_WX
        Action<OnKeyboardInputListenerResult> _inputCallback;
        Action<OnKeyboardInputListenerResult> _confirmCallback;
        Action<OnKeyboardInputListenerResult> _completeCallback;
    #endif   
        
        public void ShowKeyboard(ShowKeyboardParam param)
        {
    #if UMBREEY_SDK_WEBGL_WX
           if (_inputCallback != null || _confirmCallback != null || _completeCallback != null)
           {
               UmbreeySDK.LogError("Keyboard is already shown?");
               return;
           }
           
           WX.ShowKeyboard(new ShowKeyboardOption() {
               defaultValue = param.DefaultValue,
               maxLength = param.MaxLength,
               confirmType = param.ConfirmType,
               confirmHold = param.confirmHold,
               success =(msg)=>
               {
                   param.ShowSuccessCallBack?.Invoke();
               },
           });
           
           _inputCallback = data => param.InputCallback?.Invoke(data.value);
           WX.OnKeyboardInput(_inputCallback);
           _confirmCallback = data => param.ConfirmCallback?.Invoke();
           WX.OnKeyboardConfirm(_confirmCallback);
           _completeCallback = data => param.CompleteCallback?.Invoke();
           WX.OnKeyboardComplete(_completeCallback);
           
    #endif     
            
        }

        public void HideKeyboard()
        {
    #if UMBREEY_SDK_WEBGL_WX
            WX.HideKeyboard(new HideKeyboardOption());
            
            if (_inputCallback != null)
            {
                WX.OffKeyboardInput(_inputCallback);
                _inputCallback = null;
            }
            if (_confirmCallback != null)
            {
                WX.OffKeyboardConfirm(_confirmCallback);
                _confirmCallback = null;
            }
            if (_completeCallback != null)
            {
                WX.OffKeyboardComplete(_completeCallback);
                _completeCallback = null;
            }       
    #endif     
        }

        public void SetKeyBoardContent(string content)
        {
#if UMBREEY_SDK_WEBGL_WX
            UpdateKeyboardOption op = new UpdateKeyboardOption();
            op.value = content;
            WX.UpdateKeyboard(op);
#endif
        }
        
        public readonly int SceneIdInteractive = 7;
        public void ReportScene(int id, double? ms = null, Action<UmbreeySDK.StatusCode> callback = null)
        {
    #if UMBREEY_SDK_WEBGL_WX
            ReportSceneOption option = new ReportSceneOption()
            {
                sceneId = (double)id,
                costTime = ms,
                complete = data =>
                {
                    UmbreeySDK.Log($"WX.ReportScene {id}: {data.errMsg}");  
                },
                success = _ => callback?.Invoke(UmbreeySDK.StatusCode.Success),
                fail = _ => callback?.Invoke(UmbreeySDK.StatusCode.Failure),
            };
            WX.ReportScene(option);
    #endif
        }

        public void ReportGameStart()
        {
    #if UMBREEY_SDK_WEBGL_WX
            WX.ReportGameStart();
    #endif
        }
        
        public void ReportUserBehaviorBranchAnalytics(string branchId, bool clickEvent = false, string branchDim = null)
        {
    #if UMBREEY_SDK_WEBGL_WX
            if (string.IsNullOrEmpty(branchId))
                return;

            branchDim ??= "";
            WX.ReportUserBehaviorBranchAnalytics(branchId, branchDim, clickEvent ? 2 : 1);
    #endif            
        }
        
        public Dictionary<string,string> GetExptInfoSync(string[] keys)
        {
#if UMBREEY_SDK_WEBGL_WX
            Dictionary<string,string> obj = WX.GetExptInfoSync(keys);
            return obj;
#endif
            return new Dictionary<string, string>();
        }

        public void ReportEvent(string key,Dictionary<string,string> dic)
        {
#if UMBREEY_SDK_WEBGL_WX
           WX.ReportEvent(key,dic);
#endif
        }
        
        public override DeviceInfo GetDeviceInfo()
        {
    #if UMBREEY_SDK_WEBGL_WX
            var info = base.GetDeviceInfo();
            {
                var i = WX.GetDeviceInfo();
                info.brand = i.brand;
                info.model = FilterDeviceModel(i.model);
                info.system = i.system;
                info.platform = i.platform;
            }
            return info;
    #else
            return null;
    #endif
        }
        
        public struct MenuButtonRect
        {
            public double top;
            public double bottom;
            public double left;
            public double right;
            public double Height;
            public double Widh;
        }

        public MenuButtonRect GetMenuButtonRect()
        {
#if UMBREEY_SDK_WEBGL_WX
            var rect  = WX.GetMenuButtonBoundingClientRect();
            return new MenuButtonRect()
            {
                top = rect.top,
                bottom = rect.bottom,
                left = rect.left,
                right = rect.right,
                Height = rect.height,
                Widh = rect.width
            };
#endif
            return default;
        }
        
    #if UMBREEY_SDK_WEBGL_WX
        private Dictionary<Action<TouchInfo>, Action<OnTouchStartListenerResult>> _onTouchStartCallback;
    #endif
        public override void OnTouchEnd(Action<TouchInfo> onTouchEnd)
        {
    #if UMBREEY_SDK_WEBGL_WX
            _onTouchStartCallback ??= new Dictionary<Action<TouchInfo>, Action<OnTouchStartListenerResult>>();
            OffTouchEnd(onTouchEnd);
            if (!_onTouchStartCallback.ContainsKey(onTouchEnd))
            {
                _onTouchStartCallback[onTouchEnd] = (result) =>
                {
                    var touch = result.changedTouches.FirstOrDefault();
                    if (touch != null)
                    {
                        onTouchEnd?.Invoke(new TouchInfo()
                        {
                            identifier = touch.identifier,
                            x = touch.pageX,
                            y = touch.pageY
                        });
                    }
                };
            }
            WX.OnTouchEnd(_onTouchStartCallback[onTouchEnd]);
    #endif
        }
        
        public override void OffTouchEnd(Action<TouchInfo> onTouchEnd)
        {
    #if UMBREEY_SDK_WEBGL_WX
            if (_onTouchStartCallback != null && _onTouchStartCallback.ContainsKey(onTouchEnd))
            {
                WX.OffTouchEnd(_onTouchStartCallback[onTouchEnd]);
                _onTouchStartCallback.Remove(onTouchEnd);
            }
    #endif
        }

        public override void RequestSubscribeMessage(List<string> templateIds, Action<Dictionary<string,string>> callback)
        {
            if (templateIds == null || templateIds.Count == 0)
            {
                return;
            }

    #if UMBREEY_SDK_WEBGL_WX
            WX.RequestSubscribeMessage(new RequestSubscribeMessageOption()
            {
                tmplIds = templateIds.ToArray(),
                complete = (result) =>
                {
                    UmbreeySDK.Log($"WX.RequestSubscribeMessage complete. {result.errMsg}");
                },
                fail = (result) =>
                {
                    UmbreeySDK.LogError($"WX.RequestSubscribeMessage failure. {result.errMsg}");
                    callback?.Invoke(null);
                },
                success = (result) =>
                {
                    StringBuilder ss = new StringBuilder();
                    
                    var resultDic = new Dictionary<string, string>();
                    foreach (var data in result.Keys)
                    {
                        resultDic.Add(data, result[data]);
                        ss.Append($"{data}={result[data]} ");
                    }
                    
                    UmbreeySDK.Log($"WX.RequestSubscribeMessage success. {ss}");
                    callback?.Invoke(resultDic);
                }
            });
#endif
        }

        public void GetSubscribeSetting(Action<Dictionary<string,string>> callback)
        {
#if UMBREEY_SDK_WEBGL_WX
            WX.GetSetting(new GetSettingOption()
            {
                withSubscriptions = true,
                complete = (result) =>
                {
                    UmbreeySDK.Log($"WX.GetSetting complete. {result.errMsg}");
                },
                fail = (result) =>
                {
                    UmbreeySDK.LogError($"WX.GetSetting failure. {result.errMsg}");
                },
                success = (result) =>
                {
                    StringBuilder ss = new StringBuilder();
                    if (result.subscriptionsSetting != null)
                    {
                        foreach (var setting in result.subscriptionsSetting.itemSettings)
                        {
                            ss.Append($"{setting.Key}={setting.Value} ");
                        }
                    }
                    
                    UmbreeySDK.Log($"WX.GetSetting success. {ss}");
                    callback?.Invoke(result?.subscriptionsSetting?.itemSettings);

                }
            });
#endif
        }

        public class FeedBackButton
        {
            public string Text;
            public string Type;
            public string TextAlign;
            public string BackgroundColor;
            public string BorderColor;
            public string Color;
            public double Width;
            public double Height;
            public double Left;
            public double Top;
            public double FontSize;
            public double LineHeight;
            public Action HideAction;
            public Action ShowAction;
            public string ImageUrl;
        }
        
        public void CreateFeedbackButton(FeedBackButton fbButton)
        {
#if UMBREEY_SDK_WEBGL_WX
            CreateOpenSettingButtonOption option = new CreateOpenSettingButtonOption();
            option.text = fbButton.Text;
            option.type = fbButton.Type;
            option.image = fbButton.ImageUrl;
            option.style = new OptionStyle
            {
                // 设置按钮尺寸（基于设计分辨率）
                width = fbButton.Width,
                height = fbButton.Height,
                left =fbButton.Left,
                top = fbButton.Top,
                color = fbButton.Color,
                backgroundColor = fbButton.BackgroundColor,
                borderColor = fbButton.BorderColor,
                fontSize = fbButton.FontSize,
                lineHeight = fbButton.LineHeight, 
                textAlign = fbButton.TextAlign,
            }; 
            var button = WX.CreateFeedbackButton(option);
            fbButton.ShowAction += ()=> button.Show();
            fbButton.HideAction += ()=> button.Hide();
#endif
        }

        public class SystemInfo
        {
           public double pixelRatio;
           public double Width;
           public double Height;
        }
        public SystemInfo GetSystemInfo()
        {
#if UMBREEY_SDK_WEBGL_WX
            SystemInfo systemInfo = new SystemInfo();
            
            var wxInfo = WX.GetSystemInfoSync();
            if (wxInfo != null)
            {
                systemInfo.pixelRatio = wxInfo.pixelRatio;
                systemInfo.Width = wxInfo.screenWidth;
                systemInfo.Height = wxInfo.screenHeight;                
            }
            else
            {
                UmbreeySDK.LogError("WX.GetSystemInfoSync() return null");
            }

            return systemInfo;
#endif
            return null;

        }

        public class VideoParams
        {
            public int x;
            public int y;
            public int width;
            public int height;
            public string filePath;
            public int initialTime;
            public int playSpeed;
            public string objectFit; //fill填充  contain包含 会有空白 conver覆盖 部分可能看不到

            /// <summary>
            /// 是否显示控件
            /// </summary>
            public bool controls = true;

            /// <summary>
            /// 是否显示进度条
            /// </summary>
            public bool showProgress = false;

            public bool showProgressInControlMode =false;

            /// <summary>
            /// 是否自动播放
            /// </summary>
            public bool autoPlay = true;

            public bool loop = false;

            /// <summary>
            /// 是否禁音
            /// </summary>
            public bool muted = false;
            

            public bool obeyMuteSwitch = true;

            public bool enableProgreeGesture = false;

            public bool showCenterPlayBtn = false;

            public bool underGameView = true;
        }

        public void CreateVideo(VideoParams paraParams, Action finAction)
        {
#if UMBREEY_SDK_WEBGL_WX
            WXCreateVideoParam videoParams = new WXCreateVideoParam();
            videoParams.x = paraParams.x;
            videoParams.y = paraParams.y;
            videoParams.width = paraParams.width;
            videoParams.height = paraParams.height;
            videoParams.src = paraParams.filePath;
            videoParams.initialTime = paraParams.initialTime;
            videoParams.playbackRate = paraParams.playSpeed;
            videoParams.objectFit = paraParams.objectFit;
            videoParams.controls = paraParams.controls;
            videoParams.showProgress = paraParams.showProgress;
            videoParams.showProgressInControlMode = paraParams.showProgressInControlMode;
            videoParams.autoplay = paraParams.autoPlay;
            videoParams.loop = paraParams.loop;
            videoParams.muted = paraParams.muted;
            videoParams.obeyMuteSwitch = paraParams.obeyMuteSwitch;
            videoParams.enableProgressGesture = paraParams.enableProgreeGesture;
            videoParams.showCenterPlayBtn = paraParams.showCenterPlayBtn;
            videoParams.underGameView = paraParams.underGameView;
            
            var video = WX.CreateVideo(videoParams);
            video.OnEnded(() =>
            {
                finAction?.Invoke();
                video.Destroy();
            });
#endif
        }
        
    }
}
