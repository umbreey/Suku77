using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Umbreey
{
    [System.Serializable]
    public class ActBase
    {
        public bool Started { get; protected set; }
        public bool Finished { get; protected set; }
        
        [HideInInspector]
        public string Name;
        
        [LabelText("开始时间")] [Clamp(0,float.MaxValue)]
        public float StartTime;
        
        [LabelText("持续时间")][Clamp(0,float.MaxValue)]
        public float Duration;

        protected GameObject actor { get; private set; }
        protected ActObject actObject { get; private set; }
        protected Blackboard blackboard => actObject?.Blackboard;
        
        protected GameObject GetObjFromPath(string path)
        {
            GameObject obj = null;
            
            if (actor != null)
            {
                obj = string.IsNullOrEmpty(path) ? actor : actor.transform.Find(path)?.gameObject;
                if (obj == null)
                {
                    obj = actor.RecursivelyFind(path)?.gameObject;
                }
            }

            return obj;
        }
        
        protected virtual void OnCreated() { }
        protected virtual void OnStart(float offsetTime) { }
        protected virtual void OnUpdate(float curTime) { }
        protected virtual void OnFinish() { }
        protected virtual void OnDestroyed() { }
        protected virtual void OnPause() { }
        protected virtual void OnResume() { }

        /// <summary>
        /// 寻址跳转到指定时间点时回调（仅 ActProgressDirector 的 SeekToTime 调用）。
        /// 用于将动画/特效等外部资源对齐到目标帧，与每帧的 <see cref="OnUpdate"/> 独立。
        /// 注意：Scrub 拖拽期间由 OnUpdate 逐帧驱动采样，本回调默认忽略（可 override 自行处理）。
        /// </summary>
        protected virtual void OnSeekTo(float curTime) { }

        /// <summary>
        /// 是否处于暂停状态（由 InternalPause/InternalResume 维护，InternalDestroyed 时重置）。
        /// 异步加载完成、寻址触发的 OnStart 等晚到播放路径应据此保持暂停，避免暂停被覆盖。
        /// </summary>
        public bool Paused { get; private set; }

        /// <summary>
        /// 是否支持无销毁寻址（Scrub）。
        /// 为 true 时，ActProgressDirector 后退寻址会调用 <see cref="OnSeekReset"/> 重置状态并隐藏资源实例，
        /// 而不是销毁重建；为 false 时走销毁重建兜底路径。
        /// </summary>
        public virtual bool SupportScrub => false;

        /// <summary>
        /// 后退寻址时重置节点状态（隐藏资源实例、停止播放），但不销毁实例以便复用。
        /// 仅当 <see cref="SupportScrub"/> 为 true 时会被调用。
        /// </summary>
        protected virtual void OnSeekReset() { }
        
        /*******************************************************************\
        | Internal Methods (Don't call these methods)                       |
        \*******************************************************************/
        internal static void InternalCreated(ActBase act, ActObject actObj, GameObject actor)
        {
            if (act != null)
            {
                act.actor = actor;
                act.actObject = actObj;
                
                InternalReset(act);
                act.OnCreated();
            }
        }
        
        internal static void InternalStart(ActBase act,float offsetTime)
        {
            if (act != null)
            {
                if (!act.Started)
                {
                    act.Started = true;
                    act.OnStart(offsetTime);
                }
            }
        }
        
        internal static void InternalUpdate(ActBase act, float curTime)
        {
            act?.OnUpdate(curTime);
        }
        
        internal static void InternalFinish(ActBase act)
        {
            if (act != null)
            {
                if (!act.Finished)
                {
                    act.Finished = true;
                    act.OnFinish();
                }
            }
        }
        
        internal static void InternalDestroyed(ActBase act)
        {
            if (act != null)
            {
                act.OnDestroyed();

                act.Paused = false;
                act.actor = null;
                act.actObject = null;
            }
        }
        
        internal static void InternalReset(ActBase act)
        {
            if (act != null)
            {
                act.Started = act.Finished = false;
            }
        }

        internal static void InternalSeekReset(ActBase act)
        {
            act?.OnSeekReset();
        }

        internal static void InternalSeekTo(ActBase act, float curTime)
        {
            act?.OnSeekTo(curTime);
        }

        internal static void InternalPause(ActBase act)
        {
            if (act != null)
            {
                act.Paused = true;
                act.OnPause();
            }
        }

        internal static void InternalResume(ActBase act)
        {
            if (act != null)
            {
                act.Paused = false;
                act.OnResume();
            }
        }
    }
}
