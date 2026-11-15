using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Umbreey
{
    public abstract class ModBase
    {
        virtual protected void OnCreated(){}
        virtual protected void OnStarted(){}
        virtual protected void OnUpdate(){}
        virtual protected void OnLateUpdate(){}
        virtual protected void OnDestroyed(){}
        
        protected internal string HandleName;
        
        private float LastUpdateTime;
        private float LastLateUpdateTime;

        protected virtual bool EnableUpdate { get; set; } = false;
        protected virtual float UpdateInterval { get; set; } = 0.1f;

        protected bool UpdateTimeUnscaled { get; set; } = false;

        private ActionManager __OnAction;
        protected ActionManager m_OnAction
        {
            get
            {
                if (__OnAction == null) __OnAction = new ActionManager();
                return __OnAction;
            }
        }
        private TaskManager.Timer __OnTimer;
        protected TaskManager.Timer m_OnTimer
        {
            get
            {
                if (__OnTimer == null) __OnTimer = new TaskManager.Timer();
                return __OnTimer;
            }
        }

        protected bool Destroyed { get; private set; }
        
        
        static internal void InternalOnCreated(ModBase mod)
        {
            mod?.OnCreated();
        }
        
        static internal void InternalOnStarted(ModBase mod)
        {
            mod?.OnStarted();
        }
        
        static internal void InternalOnUpdate(ModBase mod)
        {
            if (mod is { EnableUpdate: true })
            {
                var time = mod.UpdateTimeUnscaled ? Time.unscaledTime : Time.time;
                if (time - mod.LastUpdateTime > mod.UpdateInterval)
                {
                    mod.OnUpdate();
                    mod.LastUpdateTime = time;
                }
            }
        }

        static internal void InternalOnLateUpdate(ModBase mod)
        {
            if (mod is { EnableUpdate: true })
            {
                var time = mod.UpdateTimeUnscaled ? Time.unscaledTime : Time.time;
                if (time - mod.LastLateUpdateTime > mod.UpdateInterval)
                {
                    mod.OnLateUpdate();
                    mod.LastLateUpdateTime = time;
                }
            }
        }
        
        static internal void InternalOnDestroyed(ModBase mod)
        {
            if (mod != null)
            {
                mod.__OnAction?.Clear();
                mod.__OnTimer?.Clear();
                mod.OnDestroyed();
                mod.Destroyed = true;                
            }
        }
    }
}