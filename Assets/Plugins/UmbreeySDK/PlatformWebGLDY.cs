using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Events;

#if UMBREEY_SDK_WEBGL_DY
using TTSDK;
using TTSDK.UNBridgeLib.LitJson;
#endif

namespace Umbreey
{
    public partial class PlatformWebGLDY : PlatformWebGL
    {
        internal override void Init(Action<UmbreeySDK.StatusCode> callback, params string[] param)
        {
    #if UMBREEY_SDK_WEBGL_DY
            TT.InitSDK((code, env) =>
            {
                UmbreeySDK.Log($"TT.InitSDK code:{code} launchEnum:{env.m_LaunchFromEnum} launchScene:{env.GetLaunchFromStr()} from:{env.GetLaunchFrom()} loc:{env.GetLocation()}");
                UpdateLaunchedFromSideBarCold(TT.s_ContainerEnv);
                OnGamePause(UpdateLaunchedFromSideBarCallback);
                
                if (code == 0)
                {
                    EnableRealtimeLog(true);
                    callback?.Invoke(UmbreeySDK.StatusCode.Success);
                }
                else
                {
                    UmbreeySDK.LogError($"TT.InitSDK failed: {code}");
                    callback?.Invoke(UmbreeySDK.StatusCode.Failure);
                }
            });
    #endif
        }

        internal override void Login(Action<UmbreeySDK.StatusCode, UmbreeySDK.LoginData> callback, params string[] param)
        {
    #if UMBREEY_SDK_WEBGL_DY
            TT.Login((code, anonymousCode, isLogin) =>
            {
                if (!isLogin)
                {
                    UmbreeySDK.LogWarning($"TT.Login: user not logged in. code:{code} anonymousCode:{anonymousCode}");
                }
                callback?.Invoke(UmbreeySDK.StatusCode.Success, new UmbreeySDK.LoginData()
                {
                    Token = code
                });
            }, 
                errMsg =>
            {
                UmbreeySDK.LogError($"TT.Login: {errMsg}");
                callback?.Invoke(UmbreeySDK.StatusCode.Failure, new UmbreeySDK.LoginData());
            });
    #endif   
        }
        
        internal override void Logout(Action<UmbreeySDK.StatusCode> callback, params string[] param)
        {
           
        }
        
        internal override bool SupportsLogout()
        {
            return true;
        }

        internal override void Pay(Action<UmbreeySDK.StatusCode> callback, params string[] param)
        {
       
        }
        
        internal override void Pay2(Action<UmbreeySDK.StatusCode> callback, params string[] param)
        {
          
        }
        
        internal override string GetCustomData(params string[] param)
        {
            return null;
        }

        internal override string SetCustomData(params string[] param)
        {
            return null;
        }

        internal override void OnGamePause(Action<bool> callback)
        {
    #if UMBREEY_SDK_WEBGL_DY
            TT.GetAppLifeCycle().OnHide += () =>
            {
                IsLaunchedFromSideBar = false;
                callback?.Invoke(true);
            };
            
            TT.GetAppLifeCycle().OnShow += (dict) =>
            {
                UpdateLaunchedFromSideBarHot(dict);
                callback?.Invoke(false);
            };
    #endif
        }

        internal override void QuitGame()
        {
    #if UMBREEY_SDK_WEBGL_DY     
            TT.ExitMiniProgram(false, result =>
            {
                if (result)
                    UmbreeySDK.Log("TT.ExitMiniProgram success");
                else
                    UmbreeySDK.LogError("TT.ExitMiniProgram failed");
            });
    #endif
        }

        internal override long GetAvailableFreeSpace()
        {
            return long.MaxValue;
        }

        internal override Rect GetScreenSafeArea()
        {
#if UMBREEY_SDK_WEBGL_DY
            var area = TT.GetSystemInfo().safeArea;
            Rect rect = new Rect((float)area.left, (float)area.bottom, (float)area.width, (float)area.height);
            return rect;
#else
            return base.GetScreenSafeArea();
#endif    
        }
        internal override void OpenUrl(string url)
        {
            
        }

        internal override void Vibrate()
        {
    #if UMBREEY_SDK_WEBGL_DY    
            TT.VibrateShort(new VibrateShortParam()
            {
                Fail = err =>
                {
                    UmbreeySDK.LogError($"TT.VibrateShort failed. errCode:{err.ErrorCode} errMsg:{err.ErrMsg}");
                }
            });
    #endif
        }
        
        internal override string SaveFile(string filename, byte[] data)
        {
    #if UMBREEY_SDK_WEBGL_DY    
            var path = $"{TTFileSystemManager.USER_DATA_PATH}/{filename}";
            try
            {
                var fileSystemManager = TT.GetFileSystemManager();
                fileSystemManager.WriteFile(new WriteFileParam(){ filePath = path, data = data});
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
    #if UMBREEY_SDK_WEBGL_DY 
            var path = $"{TTFileSystemManager.USER_DATA_PATH}/{filename}";
            try
            {
                var fileSystemManager = TT.GetFileSystemManager();
                fileSystemManager.Unlink(new UnlinkParam()
                {
                    filePath = path,
                    success = (err) =>
                    {
                        UmbreeySDK.Log($"TT.Unlink success. file:{filename}");
                    },
                    fail = err =>
                    {
                        UmbreeySDK.LogError($"TT.Unlink failed. file:{filename} errMsg:{err.errMsg}");
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
    #if UMBREEY_SDK_WEBGL_DY
            var filepath = texture.SaveToImageFile(filename);
            if (string.IsNullOrEmpty(filepath))
            {
                UmbreeySDK.LogError($"save texture to image file failed. texture:{texture.name} filename:{filename}");
                callback?.Invoke(UmbreeySDK.StatusCode.Failure);
                return;
            }
            
            var option = new TTSaveImageToPhotosAlbumParam();
            option.filePath = filepath;
            option.complete = () =>
            {
                UmbreeySDK.Log($"TT.SaveImageToPhotosAlbum complete");
                UmbreeySDK.DeleteFile(filename); // 删除临时文件
            };
            option.success = res =>
            {
                UmbreeySDK.Log("TT.SaveImageToPhotosAlbum success");
                callback?.Invoke(UmbreeySDK.StatusCode.Success);
            };
            option.fail = res =>
            {
                UmbreeySDK.LogError($"TT.SaveImageToPhotosAlbum failed: {res}");
                callback?.Invoke(UmbreeySDK.StatusCode.Failure);
            };
            TT.SaveImageToPhotosAlbum(option);
    #endif
        }
        
        
        //////////////////////////////////////////////////////////////////////////////////////////////////////////
        /// Below is the code for override webgl platform
        //////////////////////////////////////////////////////////////////////////////////////////////////////////
        public override void CreateUserInfoButton(Action<UmbreeySDK.StatusCode, UserData> callback, UnityEngine.UI.Button button, Camera uiCamera = null)
        {
    #if UMBREEY_SDK_WEBGL_DY
            if (button != null)
            {
                UnityAction clickHandler = null;
                clickHandler = () =>
                {
                    if (button != null) // 防止按钮被销毁
                    {
                        button.onClick.RemoveListener(clickHandler);
                        GetUserInfo(callback, true);
                    }
                };
                
                button.onClick.AddListener(clickHandler);
            }
            else
            {
                IEnumerator DelayedOffTouchStart(TTInput.TouchEventCallback handler)
                {
                    yield return null; // 等待一帧
                    TT.OffTouchStart(handler);
                }
                
                TTInput.TouchEventCallback touchHandler = null;
                touchHandler = (touch) =>
                {
                    UmbreeySDK.StartCoroutine(DelayedOffTouchStart(touchHandler)); // 触发后y延时取消监听，避免遍历回调时修改回调列表
                    GetUserInfo(callback, true);
                };
                TT.OnTouchStart(touchHandler);
            }
            
    #endif
        }

        public override void GetUserInfo(Action<UmbreeySDK.StatusCode, UserData> callback, bool openSetting = false)
        {
    #if UMBREEY_SDK_WEBGL_DY
            TT.GetSetting(settings =>
            {
                // 若用户曾经拒绝授权，引导到设置界面开启，让下次再获取用户信息
                if (settings.TryGetValue(AuthorizeScope.UserInfo, out var authorized) && !authorized)
                {
                    if (openSetting) // 打开设置界面引导开启授权
                    {
                        TT.OpenSetting(o =>
                        {
                            UmbreeySDK.Log($"TT.OpenSetting success. userInfo:{o.UserInfo}");
                            callback?.Invoke(UmbreeySDK.StatusCode.Cancel, new UserData());
                        }, errMsg =>
                        {
                            UmbreeySDK.LogError($"TT.OpenSetting errMsg:{errMsg}");
                            callback?.Invoke(UmbreeySDK.StatusCode.Failure, new UserData());
                        });                        
                    }
                    else
                    {
                        callback?.Invoke(UmbreeySDK.StatusCode.Cancel, new UserData());
                    }
                }
                else // 用户已同意授权，或者用户未授权过，直接执行获取用户信息
                {
                    TT.GetUserInfo(false,  (ref TTUserInfo data) =>
                    {
                        UmbreeySDK.Log($"TT.GetUserInfo: {data}");
                        var userData = new UserData()
                        {
                            NickName = data.nickName,
                            AvatarUrl = data.avatarUrl,
                            Gender = data.gender
                        };
                        callback?.Invoke(UmbreeySDK.StatusCode.Success, userData);
                    }, errMsg =>
                    {
                        UmbreeySDK.LogError($"TT.GetUserInfo errMsg:{errMsg}");
                        callback?.Invoke(UmbreeySDK.StatusCode.Failure, new UserData());
                    });
                }
            }, errMsg =>
            {
                UmbreeySDK.LogError($"TT.GetSetting errMsg:{errMsg}");
                callback?.Invoke(UmbreeySDK.StatusCode.Failure, new UserData());
            });
    #endif
        }
        
        public override DeviceInfo GetDeviceInfo()
        {
    #if UMBREEY_SDK_WEBGL_DY
            var info = base.GetDeviceInfo();
            {
                var i = TT.GetSystemInfo();
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

    #if UMBREEY_SDK_WEBGL_DY
        private Dictionary<Action<TouchInfo>, TTInput.TouchEventCallback> _onTouchStartCallback;
    #endif
        public override void OnTouchEnd(Action<TouchInfo> onTouchEnd)
        {
    #if UMBREEY_SDK_WEBGL_DY
            _onTouchStartCallback ??= new Dictionary<Action<TouchInfo>, TTInput.TouchEventCallback>();
            OffTouchEnd(onTouchEnd);
            if (!_onTouchStartCallback.ContainsKey(onTouchEnd))
            {
                _onTouchStartCallback[onTouchEnd] = (result) =>
                {
                    if (result.changedTouches.Any())
                    {
                        var touch = result.changedTouches.FirstOrDefault();
                        onTouchEnd?.Invoke(new TouchInfo() 
                        {
                            identifier = touch.identifier,
                            x = touch.screenX,
                            y = touch.screenY
                        });
                    }
                };
            }
            TT.OnTouchEnd(_onTouchStartCallback[onTouchEnd]);
    #endif
        }
        
        public override void OffTouchEnd(Action<TouchInfo> onTouchEnd)
        {
    #if UMBREEY_SDK_WEBGL_DY
            if (_onTouchStartCallback != null && _onTouchStartCallback.ContainsKey(onTouchEnd))
            {
                TT.OffTouchEnd(_onTouchStartCallback[onTouchEnd]);
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
    #if UMBREEY_SDK_WEBGL_DY
            TT.RequestSubscribeMessage(
                templateIds,
                (result) =>
                {
                    StringBuilder ss = new StringBuilder();
                    
                    var resultDic = new Dictionary<string, string>();
                    foreach (var data in result.Keys)
                    {
                        resultDic.Add(data, result[data]);
                        ss.Append($"{data}={result[data]} ");
                    }
                    
                    UmbreeySDK.Log($"TT.RequestSubscribeMessage success. {ss}");
                    callback?.Invoke(resultDic);
                },() =>
                {
                    UmbreeySDK.Log($"TT.RequestSubscribeMessage complete.");
                }, (errCode, errMsg) =>
                {
                    UmbreeySDK.LogError($"TT.RequestSubscribeMessage failure. errCode:{errCode} errMsg:{errMsg}");
                    callback?.Invoke(null);
                });
    #endif
        }
        
        public override LaunchOptions GetLaunchOptions() // 注意：只在冷启动时更新，热启动不会更新
        {
    #if UMBREEY_SDK_WEBGL_DY
            var options = TT.GetLaunchOptionsSync();
            return new LaunchOptions()
            {
                scene = options.Scene,
                query = options.Query
            };
    #else
            return null;
    #endif
        }
        
        public override void SetPreferredFramesPerSecond(int fps)
        {
    #if UMBREEY_SDK_WEBGL_DY        
             TT.SetPreferredFramesPerSecond(fps);
    #endif           
        }
		
        public override float GetDevicePixelRatio()
        {
    #if UMBREEY_SDK_WEBGL_DY        
            var systemInfo = TT.GetSystemInfo();
            return (float)systemInfo.pixelRatio;
    #else
            return 1.0f; // 默认值
    #endif
        }
        
        public override void SetDevicePixelRatio(float dpr)
        {
    #if UMBREEY_SDK_WEBGL_DY
            TT.SetPreferredDevicePixelRatio(dpr);
    #endif
        }
        
        
        //////////////////////////////////////////////////////////////////////////////////////////////////////////
        /// Below is the code for douyin platform only
        //////////////////////////////////////////////////////////////////////////////////////////////////////////
        private const string kSideBarScene = "021036"; // 场景值：https://developer.open-douyin.com/docs/resource/zh-CN/mini-game/develop/framework/scene-value/
        private const string kSideBarLaunchFrom = "homepage"; // https://developer.open-douyin.com/docs/resource/zh-CN/mini-game/develop/guide/open-ability/Introduction-for-tech
        private const string kSideBarLocation = "sidebar_card";
        public bool IsLaunchedFromSideBar { get; private set; } 
        
        public string GetContainerEnv()
        {
    #if UMBREEY_SDK_WEBGL_DY
            var env = TT.s_ContainerEnv;
            return JsonUtility.ToJson(env);
    #else
            return "{}";
    #endif
        }

    #if UMBREEY_SDK_WEBGL_DY
        private void UpdateLaunchedFromSideBarHot(Dictionary<string, object> onShowDict) // 热启动
        {
            IsLaunchedFromSideBar = false;

            var homepage = false;
            var sidebar = false;
            foreach (var (key,val) in onShowDict)
            {
                if (key == "scene" && val.ToString() == kSideBarScene)
                {
                    IsLaunchedFromSideBar = true;
                    break;
                }

                if (key == "launch_from" && val.ToString() == kSideBarLaunchFrom)
                {
                    homepage = true;
                }
                else if (key == "location" && val.ToString() == kSideBarLocation)
                {
                    sidebar = true;
                }
            }
            
            IsLaunchedFromSideBar |= (homepage && sidebar);
        }
        
        private void UpdateLaunchedFromSideBarCold(ContainerEnv env) // 冷启动
        {
            IsLaunchedFromSideBar = false;
            
            if (env.GetLaunchFromStr() == kSideBarScene)
            {
                IsLaunchedFromSideBar = true;
            }
            else
            {
                if (env.GetLaunchFrom() == kSideBarLaunchFrom && env.GetLocation() == kSideBarLocation)
                {
                    IsLaunchedFromSideBar = true;
                }                
            }
        }
        
        private void UpdateLaunchedFromSideBarCallback(bool paused)
        {
            UmbreeySDK.Log($"UpdateLaunchedFromSideBarCallback paused:{paused} launchedFromSideBar:{IsLaunchedFromSideBar}");
        }
    #endif // UMBREEY_SDK_WEBGL_DY

        public void CheckSideBar(Action<UmbreeySDK.StatusCode> callback)
        {
    #if UMBREEY_SDK_WEBGL_DY          
            TT.CheckScene(TTSideBar.SceneEnum.SideBar, result =>
            {
                callback?.Invoke(result ? UmbreeySDK.StatusCode.Success : UmbreeySDK.StatusCode.Failure);
            }, () =>
            {
                // do nothing on complete
            }, (errCode, errMsg) =>
            {
                UmbreeySDK.LogError($"TT.CheckScene SideBar errCode:{errCode}, errMsg:{errMsg}");
                callback?.Invoke(UmbreeySDK.StatusCode.Fallback);
            });
    #endif
        }

        public void NavigateToSideBar(Action<UmbreeySDK.StatusCode> callback)
        {
    #if UMBREEY_SDK_WEBGL_DY 
            var data = new JsonData {
                ["scene"] = "sidebar"
            };
            
            TT.NavigateToScene(data, () =>
            {
                callback?.Invoke(UmbreeySDK.StatusCode.Success);
            }, () =>
            {
                UmbreeySDK.Log("TT.NavigateToScene completed");
            }, (errCode, errMsg) =>
            {
                UmbreeySDK.LogError($"TT.NavigateToScene failed. errCode:{errCode}, errMsg:{errMsg}");
                callback?.Invoke(UmbreeySDK.StatusCode.Failure);
            });
    #endif
        }
    }
    
}
