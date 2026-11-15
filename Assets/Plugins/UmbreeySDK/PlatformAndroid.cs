using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using J2CSCallback = System.Action<System.Collections.Generic.List<string>>;

namespace Umbreey
{
    public class PlatformAndroid : PlatformBase
    {
        public enum ChannelType
        {
            None,
            _233,
            Bilibili,
            Douyin,
            Taptap,
            Phone,
            Google,
            Discord,
            Paytest,
        }
        public ChannelType Channel { get; private set; } = ChannelType.None;
        
        private readonly AndroidJavaObject _activity;
        private readonly AndroidJavaObject _vibrator;
        
        public PlatformAndroid()
        {
            _j2csMono = UmbreeySDK.RootObj.AddComponent<J2CSMono>();
            _j2csMono.callFromJava += this.OnCallFromJava;
            
            AndroidJavaClass jc = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            _activity = jc.GetStatic<AndroidJavaObject>("currentActivity");
            
            string c = _activity.Call<string>("GetChannel");
            if (!string.IsNullOrEmpty(c))
            {
                Channel = (ChannelType)Enum.Parse(typeof(ChannelType), c);
            }
            
            try
            {
                using AndroidJavaObject app = _activity.Call<AndroidJavaObject>("getApplication");
                if (app != null)
                {
                    _vibrator = app.Call<AndroidJavaObject>("getSystemService", "vibrator");
                }
            }
            catch (Exception e)
            {
                UmbreeySDK.LogError($"constructor get vibrator failed: {e.Message}");
            }
        }
        
        public override string OnKeyOperatorType()
        {
            return _activity.Call<string>("OnKeyOperatorType");
        }
	    
        internal override void Init(Action<UmbreeySDK.StatusCode> callback, params string[] param)
        {
            _activity.Call("OnInit", param);
            callback?.Invoke(UmbreeySDK.StatusCode.Success);
        }

        internal override bool IsStartPolicy()
        {
            try
            {
                using AndroidJavaClass jc = new AndroidJavaClass("com.unity3d.player.Wakoo");
                return jc.CallStatic<bool>("IsStartPolicy");
            }
            catch (Exception e)
            {
                UmbreeySDK.LogError($"IsStartPolicy failed: {e.Message}");
            }

            return false;
        }

        internal override void Login(Action<UmbreeySDK.StatusCode, UmbreeySDK.LoginData> callback, params string[] param)
        {
            RegCallFromJavaOnce("Login.OnLoginSuccess", data =>
            {
                var status = UmbreeySDK.StatusCode.Success;
                
                var loginData = new UmbreeySDK.LoginData();
                if (Channel == ChannelType._233)
                {
                    loginData.Token = data[0];
                    loginData.Name = data[1];
                }
                else if (Channel == ChannelType.Bilibili)
                {
                    loginData.Token = data[0];
                }
                else if (Channel == ChannelType.Douyin)
                {
                    loginData.Token = data[0];
                }
                else if (Channel == ChannelType.Taptap)
                {
                    loginData.Token = data[0];
                    loginData.Name = data[1]; // MacKey
                }
                else if (Channel == ChannelType.Phone)
                {
                    loginData.Token = data[0];
                }
                else if (Channel == ChannelType.Google)
                {
                    loginData.Token = data[0]; // Google idToken
                    loginData.Name = data[1];  // Google uid
                }
                else // None channel case, just return default login data successfully
                {
                    loginData = UmbreeySDK.LoginData.Default;
                }

                callback?.Invoke(status, loginData);
            });
            
            RegCallFromJavaOnce("Login.OnLoginCancel", data =>
            {
                var funcCode = data.FirstOrDefault() == "SWITCH_ACCOUNT"
                    ? UmbreeySDK.LoginFuncCode.SWITCH_ACCOUNT
                    : UmbreeySDK.LoginFuncCode.None;
                
                callback?.Invoke(UmbreeySDK.StatusCode.Cancel, new UmbreeySDK.LoginData() { FuncCode = funcCode });
            });
            
            RegCallFromJavaOnce("Login.OnLoginFailure", data =>
            {
                var funcCode = data.FirstOrDefault() == "AGE_LIMIT" ? UmbreeySDK.LoginFuncCode.AGE_LIMIT : UmbreeySDK.LoginFuncCode.None;
                callback?.Invoke(UmbreeySDK.StatusCode.Failure, new UmbreeySDK.LoginData() { FuncCode = funcCode});
            });
            
            _activity.Call("OnLogin", param);
        }
        
        internal override void Logout(Action<UmbreeySDK.StatusCode> callback, params string[] param)
        {
            RegCallFromJavaOnce("Logout.OnLogoutSuccess", data => callback?.Invoke(UmbreeySDK.StatusCode.Success));
            RegCallFromJavaOnce("Logout.OnLoginCancel", data => callback?.Invoke(UmbreeySDK.StatusCode.Cancel));
            RegCallFromJavaOnce("Logout.OnLoginFailure", data => callback?.Invoke(UmbreeySDK.StatusCode.Failure));
            
            _activity.Call("OnLogout", param);
        }
        
        internal override bool SupportsLogout()
        {
            return _activity.Call<bool>("CanLogout");
        }

        internal override void Pay(Action<UmbreeySDK.StatusCode> callback, params string[] param)
        {
            RegCallFromJavaOnce("OnPaySuccess", data => callback?.Invoke(UmbreeySDK.StatusCode.Success));
            RegCallFromJavaOnce("OnPayCancel", data => callback?.Invoke(UmbreeySDK.StatusCode.Cancel));
            RegCallFromJavaOnce("OnPayError", data => callback?.Invoke(UmbreeySDK.StatusCode.Failure));
            
            _activity.Call("OnPay", param);
        }
        
        internal override void Pay2(Action<UmbreeySDK.StatusCode> callback, params string[] param)
        {
            RegCallFromJavaOnce("OnPay2Success", data => callback?.Invoke(UmbreeySDK.StatusCode.Success));
            RegCallFromJavaOnce("OnPay2Cancel", data => callback?.Invoke(UmbreeySDK.StatusCode.Cancel));
            RegCallFromJavaOnce("OnPay2Error", data => callback?.Invoke(UmbreeySDK.StatusCode.Failure));
            
            _activity.Call("OnPay2", param);
        }
        
        internal override void Share(Action<UmbreeySDK.StatusCode> callback, UmbreeySDK.ShareParams param)
        {
            RegCallFromJavaOnce("Share.OnShareSuccess", data => callback?.Invoke(UmbreeySDK.StatusCode.Success));
            RegCallFromJavaOnce("Share.OnShareCancel", data => callback?.Invoke(UmbreeySDK.StatusCode.Cancel));
            RegCallFromJavaOnce("Share.OnShareFailure", data => callback?.Invoke(UmbreeySDK.StatusCode.Failure));
            
            var arrays = new string[]
            {
                param.Title,
                param.Url,
                param.Desc,
            };
            _activity.Call("OnShare", arrays);
        }

        public void ShareToWechat(Action<UmbreeySDK.StatusCode> callback, string title, string desc, string url, bool timeline = false)
        {
            var arrays = new string[]
            {
                timeline ? "1" : "0",
                title ?? string.Empty,
                desc ?? string.Empty,
                url ?? string.Empty,
            };
            _activity.Call("OnWeiChatShare", arrays);
        }

        public void ShareToQQ(Action<UmbreeySDK.StatusCode> callback, string title, string desc, string url, bool timeline = false, string link = null)
        {
            if (timeline)
            {
                RegCallFromJavaOnce("OnQQTimelineSuccess", data => callback?.Invoke(UmbreeySDK.StatusCode.Success));
                RegCallFromJavaOnce("OnQQTimelineCancel", data => callback?.Invoke(UmbreeySDK.StatusCode.Cancel));
                RegCallFromJavaOnce("OnQQTimelineError", data => callback?.Invoke(UmbreeySDK.StatusCode.Failure));
            }
            else
            {
                RegCallFromJavaOnce("OnQQFriendSuccess", data => callback?.Invoke(UmbreeySDK.StatusCode.Success));
                RegCallFromJavaOnce("OnQQFriendCancel", data => callback?.Invoke(UmbreeySDK.StatusCode.Cancel));
                RegCallFromJavaOnce("OnQQFriendError", data => callback?.Invoke(UmbreeySDK.StatusCode.Failure));            
            }
            
            var arrays = new string[]
            {
                timeline ? "1" : "0",
                title ?? string.Empty,
                desc ?? string.Empty,
                url ?? string.Empty,
                timeline ? link ?? string.Empty : string.Empty,
            };
            _activity.Call("OnQQShare", arrays);
        }

        internal bool IsInstalledWeichat()
        {
            return _activity.Call<bool>("IsInstallWeiChat");
        }

        internal bool IsInstalledQQ()
        {
            return _activity.Call<bool>("IsInstallQQ");
        }

        internal override string GetCustomData(params string[] param)
        {
            return _activity.Call<string,string>("GetInfo", param);
        }

        internal override string SetCustomData(params string[] param)
        {
            return _activity.Call<string,string>("SetInfo", param);
        }

        public override DeviceInfo GetDeviceInfo()
        {
            var info = base.GetDeviceInfo();
            
            try
            {
                using (var buildClass = new AndroidJavaClass("android.os.Build"))
                using (var versionClass = new AndroidJavaClass("android.os.Build$VERSION"))
                {
                    info.brand = buildClass.GetStatic<string>("BRAND");
                    info.manufacturer = buildClass.GetStatic<string>("MANUFACTURER");
                    info.osVersion = versionClass.GetStatic<string>("RELEASE");
                }
            }
            catch (Exception e)
            {
                UmbreeySDK.LogError($"GetDeviceInfo failed: {e.Message}");
            }

            return info;
        }

        internal override long GetAvailableFreeSpace()
        {
            try
            {
                AndroidJavaObject statFs = new AndroidJavaObject( "android.os.StatFs", Application.persistentDataPath);
                long blockSize = statFs.Call<long>("getBlockSizeLong");
                long availableBlocks = statFs.Call<long>("getAvailableBlocksLong");

                return availableBlocks * blockSize;
            }
            catch (Exception e)
            {
                UmbreeySDK.LogError($"GetAvailableFreeSpace failed: {e.Message}");
            }

            return 0; 
        }

        internal override void OpenUrl(string url)
        {
            _activity.Call("OpenUrl", url);
        }

        internal override void Vibrate()
        {
            if (_vibrator == null) return;
            try
            {
                //_vibrator.Call("vibrate", 20L);
                //_vibrator.Call("vibrate", 45L);
                //_vibrator.Call("vibrate", 85L);
                _vibrator.Call("vibrate", 20L);
            }
            catch (Exception e)
            {
                UmbreeySDK.LogError($"Vibrate failed: {e.Message}");
            }
        }
        
        internal override void SaveImageToPhotoAlbum(Action<UmbreeySDK.StatusCode> callback, Texture2D texture, string filename, string album)
        {
            if (string.IsNullOrEmpty(album))
            {
                album = $"{Application.productName}";
            }

            try
            {
#if UMBREEY_SDK_NATIVE_GALLERY
                NativeGallery.SaveImageToGallery(texture, album, filename, (success, path) =>
                {
                    if (success)
                    {
                        UmbreeySDK.Log($"SaveImageToPhotoAlbum success. texture:{texture.name} filename:{filename}");
                        callback?.Invoke(UmbreeySDK.StatusCode.Success);
                    }
                    else
                    {
                        UmbreeySDK.LogError($"SaveImageToPhotoAlbum failed. texture:{texture.name} filename:{filename}");
                        callback?.Invoke(UmbreeySDK.StatusCode.Failure);
                        
                    }
                });
#else
                // 本工程未接入 NativeGallery：退回写到持久化目录，不进系统相册
                var path = SaveFile(filename, texture.EncodeToPNG());
                if (!string.IsNullOrEmpty(path))
                {
                    UmbreeySDK.LogWarning($"SaveImageToPhotoAlbum fallback to file (no NativeGallery): {path}");
                    callback?.Invoke(UmbreeySDK.StatusCode.Success);
                }
                else
                {
                    callback?.Invoke(UmbreeySDK.StatusCode.Failure);
                }
#endif
            }
            catch (Exception e)
            {
                UmbreeySDK.LogError($"SaveImageToPhotoAlbum failed. texture:{texture.name} filename:{filename} errMsg:{e.Message}");
                callback?.Invoke(UmbreeySDK.StatusCode.Failure);
                throw;
            }
        }

        public string GetCountry()
        {
            try
            {
                using (AndroidJavaClass localeClass = new AndroidJavaClass("java.util.Locale"))
                using (AndroidJavaObject defaultLocale = localeClass.CallStatic<AndroidJavaObject>("getDefault")) 
                { 
                    string country = defaultLocale.Call<string>("getCountry"); // 返回 "US"
                    return country;
                } 
            }
            catch (Exception e)
            {
                UmbreeySDK.LogError($"GetCountry failed: {e.Message}");
            }

            return string.Empty;
        }
        
    #region Java To C#
        public class J2CSMono : MonoBehaviour
        {
            public event Action<string, List<string>> callFromJava;
            public void JavaCallCSharp(string msg)
            {
                UmbreeySDK.Log($"Java -> C#: {msg}");
                
                var array = new List<string>(msg.Split("##"));
                if (array.Count > 0)
                {
                    string key = array[0];
                    array.RemoveAt(0);
                        
                    callFromJava?.Invoke(key, array);
                }
            }
        }
        
        private J2CSMono _j2csMono;
        private Dictionary<string, J2CSCallback> _j2csCallbacks = new Dictionary<string, J2CSCallback>();
        private Dictionary<string, J2CSCallback> _j2csCallbacksOnce = new Dictionary<string, J2CSCallback>();
        
        public void SimulateJavaCallCSharp(params string[] param)
        {
            _j2csMono.JavaCallCSharp(string.Join("##", param));
        }
        
        public void UnRegCallFromJava(string key)
        {
            _j2csCallbacksOnce.Remove(key);
            _j2csCallbacks.Remove(key);
        }
        
        public void RegCallFromJavaOnce(string key, J2CSCallback call)
        {
            InternalRegCallFromJava(key, call, _j2csCallbacksOnce);
        }
        
        public void RegCallFromJava(string key, J2CSCallback call)
        {
            InternalRegCallFromJava(key, call, _j2csCallbacks);
        }
        
        private static void InternalRegCallFromJava(string key, J2CSCallback call, Dictionary<string, J2CSCallback> dict)
        {
            if (dict.ContainsKey(key)) 
            {
                dict[key] -= call;
                dict[key] += call;
            }
            else
            {
                dict[key] = call;
            }
        }
        
        private void OnCallFromJava(string key, List<string> data)
        {
            if (_j2csCallbacksOnce.TryGetValue(key, out var callOnce))
            {
                // 如果key包含.字符，删除所有该前缀的回调
                int dotIndex = key.IndexOf('.');
                if (dotIndex > 0)
                {
                    string prefix = key.Substring(0, dotIndex + 1); // 包含.字符
                    var keysToRemove = _j2csCallbacksOnce.Keys.Where(k => k.StartsWith(prefix)).ToList();
                    foreach (var k in keysToRemove)
                    {
                        _j2csCallbacksOnce.Remove(k);
                    }
                }
                else
                {
                    // 没有.字符，保持现有逻辑
                    _j2csCallbacksOnce.Remove(key);
                }
                
                callOnce?.Invoke(data);
            }
            
            if (_j2csCallbacks.TryGetValue(key, out var call))
            {
                call?.Invoke(data);
            }
        }
    #endregion   
        
    }    
}

