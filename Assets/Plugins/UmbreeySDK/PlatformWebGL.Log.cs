using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Umbreey
{
    public partial class PlatformWebGL
    {
        public virtual void I(string str) { }
        public virtual void W(string str) { }
        public virtual void E(string str) { }
        public virtual void AddFilterMsg(string tag) { }
        public virtual void SetFilterMsg(string tag) { }
        
        public bool RealtimeLogEnabled { get; protected set; } = false;
        
        public virtual void EnableRealtimeLog(bool enable)
        {
            RealtimeLogEnabled = enable;
            
            Application.logMessageReceived -= OnApplicationLogMessageReceived;
            if (enable)
            {
                Application.logMessageReceived += OnApplicationLogMessageReceived;
            }
        }
        
        private void OnApplicationLogMessageReceived(string str, string stackTrace, LogType type)
        {
            if (!RealtimeLogEnabled) return;
            switch (type)
            {
                case LogType.Error:
                case LogType.Assert:
                case LogType.Exception:
                    E(str);
                    break;
                case LogType.Warning:
                    W(str);
                    break;
                case LogType.Log:
                    I(str);
                    break;
                default:
                    break;
            }
        }
    }
    
}
