using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Umbreey
{
    public class ActPlayable
    {
        public uint Id;
        public string Name;
        
        private GameObject _actor;

        public GameObject Actor
        {
            get => _actor;
            set
            {
                _actor = value;
                CacheIsValid();
            }
        }

        private ActObject _actObj;
        public ActObject ActObj
        {
            get => _actObj;
            set
            {
                _actObj = value;
                CacheIsValid();
            }
        }

        public float BeginTime;
        public float BeginOffset;
        public Action<bool> OnFinishCallback;

        public bool IsPaused { get; private set; }
        private float _pauseTime;

        /// <summary>
        /// 暂停播放，记录暂停时刻。重复调用无效。
        /// </summary>
        public void Pause()
        {
            if (IsPaused) return;
            IsPaused = true;
            _pauseTime = Time.time;
        }

        /// <summary>
        /// 从暂停处继续播放，补偿 BeginTime。重复调用无效。
        /// </summary>
        public void Resume()
        {
            if (!IsPaused) return;
            IsPaused = false;
            BeginTime += Time.time - _pauseTime;
        }

        /// <summary>
        /// 异步加载完成时调用，若当前已暂停则将 BeginTime 对齐到暂停时刻，
        /// 避免加载耗时被计入播放进度。
        /// </summary>
        public void SetBeginTimeOnLoaded()
        {
            // 未暂停时：BeginTime = Time.time（正常开始）
            // 已暂停时：BeginTime = _pauseTime，使已流逝进度为 0，
            //           Resume 时 BeginTime += (Time.time - _pauseTime) 仍正确
            BeginTime = IsPaused ? _pauseTime : Time.time;
        }

        public bool IsValid { get; private set; }
        private void CacheIsValid()
        {
            IsValid = ActObj != null && Actor != null;
        }
    }

    public class ActDirector
    {
        private static Dictionary<uint, ActPlayable> s_ActPlayables = new Dictionary<uint, ActPlayable>();
        private static Dictionary<uint, ActPlayable> s_addPlayables = new Dictionary<uint, ActPlayable>();
        private static Dictionary<uint, ActPlayable> s_remPlayables = new Dictionary<uint, ActPlayable>();
        private static bool s_isUpdating;
        private static uint s_idCounter;


        static ActDirector()
        {
            TaskManager.Update(Update);
        }
        
        private static TaskManager.Timer __OnTimer;
        protected static TaskManager.Timer m_OnTimer
        {
            get
            {
                if (__OnTimer == null) __OnTimer = new TaskManager.Timer();
                return __OnTimer;
            }
        }
        
        public static void DestroyTimeout(UnityEngine.Object obj, float timeout)
        {
            if (obj == null)
                return;
            
            m_OnTimer.Timeout(() =>
            {
                if (obj)
                {
                    Object.Destroy(obj);
                }
            }, timeout);
        }
        
        public static ActPlayable GetPlayable(uint actId)
        {
            if (s_remPlayables.ContainsKey(actId))
                return null;

            if (s_addPlayables.TryGetValue(actId, out ActPlayable addplayable))
                return addplayable;

            s_ActPlayables.TryGetValue(actId, out ActPlayable playable);
            return playable;
        }

        private static Dictionary<uint, ActPlayable> GetCurrentPlayables()
        {
            return s_isUpdating ? s_addPlayables : s_ActPlayables;
        }

        #region 只读枚举

        // 两条独立缓冲：AllPlayables 与 GetActorPlayables 各用一条，
        // 避免"遍历 AllPlayables 时又调 GetActorPlayables"把外层快照冲掉。
        // 返回的是内部缓冲：禁止缓存、禁止跨帧持有、禁止在遍历体内再次触发枚举。
        private static readonly List<ActPlayable> s_AllBuffer = new List<ActPlayable>();
        private static readonly List<ActPlayable> s_ActorBuffer = new List<ActPlayable>();

        /// <summary>
        /// 所有在播 Playable 的快照（主表 + add 表，剔除已 Stop 的 id），
        /// 与 <see cref="GetPlayable"/> 的合并语义一致。
        /// 含 ActObj 仍在异步加载中的 Playable（IsValid=false）——批量 Stop 必须能停掉它们，
        /// 否则加载完成后会变成无人管理的僵尸 Playable。
        /// </summary>
        public static List<ActPlayable> AllPlayables => Collect(s_AllBuffer, null);

        /// <summary>按 Actor 过滤。actor 为 null 或已销毁时返回空集合。</summary>
        public static List<ActPlayable> GetActorPlayables(GameObject actor) => Collect(s_ActorBuffer, actor);

        /// <summary>无共享缓冲版本，重入安全，调用方自备容器。业务层批量操作请用这个。</summary>
        public static void CopyAllTo(List<ActPlayable> dst) => Collect(dst, null);

        /// <summary>无共享缓冲版本，重入安全，调用方自备容器。业务层批量操作请用这个。</summary>
        public static void CopyActorPlayablesTo(GameObject actor, List<ActPlayable> dst) => Collect(dst, actor);

        public static int PlayableCount
        {
            get
            {
                var count = 0;
                foreach (var kv in s_ActPlayables)
                {
                    if (!s_remPlayables.ContainsKey(kv.Key))
                        count++;
                }

                foreach (var kv in s_addPlayables)
                {
                    if (!s_remPlayables.ContainsKey(kv.Key))
                        count++;
                }

                return count;
            }
        }

        /// <summary>
        /// actor 为 null 表示不过滤（取全部）；否则按 Actor 精确匹配。
        /// Unity 重载的 == 使"已销毁 GameObject"等于 null，所以 !actor 这一个判断
        /// 同时挡掉"传了 null"和"传了已销毁对象"两种输入。
        /// </summary>
        private static List<ActPlayable> Collect(List<ActPlayable> dst, GameObject actor)
        {
            dst.Clear();

            // 传了 actor 但已销毁（或传了 null 又要求过滤）→ 返回空，避免"两个已销毁对象
            // m_CachedPtr 都是 0 被判相等"把所有僵尸 Playable 都捞出来
            var byActor = !ReferenceEquals(actor, null);
            if (byActor && !actor)
                return dst;

            CollectTable(s_ActPlayables, dst, actor, byActor);
            CollectTable(s_addPlayables, dst, actor, byActor);
            return dst;
        }

        private static void CollectTable(Dictionary<uint, ActPlayable> table, List<ActPlayable> dst,
            GameObject actor, bool byActor)
        {
            foreach (var kv in table)
            {
                if (s_remPlayables.ContainsKey(kv.Key))
                    continue;
                if (byActor && kv.Value.Actor != actor)
                    continue;
                dst.Add(kv.Value);
            }
        }

        #endregion

        public static uint Play(string assetName, GameObject actor,bool needLoop = false)
        {
            return Play(assetName, 0f, actor, null, null,needLoop);
        }
        
        public static uint Play(string assetName, float startTime, GameObject actor)
        {
            return Play(assetName, startTime, actor, null, null);
        }
        
        public static uint Play(string assetName, GameObject actor, Action<bool> onFinish,bool needLoop = false)
        {
            return Play(assetName,0f, actor, null, onFinish,needLoop);
        }
        
        public static uint Play(string assetName, GameObject actor, Action<ActPlayable> onPlay, Action<bool> onFinish)
        {
            return Play(assetName, 0f, actor, onPlay, onFinish);
        }
        
        public static uint Play(string assetName, float startTime, GameObject actor, Action<bool> onFinish)
        {
            return Play(assetName, startTime, actor, null, onFinish);
        }
        
        public static uint Play(string assetName,float startTime, GameObject actor,Action<ActPlayable> onPlay, Action<bool> onFinish,bool needLoop = false)
        {
            if (string.IsNullOrEmpty(assetName))
                return 0;
            
            var playable = new ActPlayable()
            {
                Id = ++s_idCounter,
                Name = assetName,
                Actor = actor,
                OnFinishCallback = onFinish,
                BeginOffset = Mathf.Max(0f,startTime),
            };
            GetCurrentPlayables().Add(playable.Id, playable);

            Assets.InstantiateAsync<ActObject>($"{assetName}.asset", (o =>
            {
                if (needLoop)
                {
                    o.Loop = true;
                }
                
                if (GetPlayable(playable.Id) != null)
                {
                    playable.ActObj = o;
                    playable.SetBeginTimeOnLoaded(); // 若加载期间已暂停，BeginTime 对齐到暂停时刻

                    foreach (var act in o.ActList)
                    {
                        ActBase.InternalCreated(act, o, actor);
                        if (act != null && act.StartTime == 0)
                            ActBase.InternalStart(act, playable.BeginOffset);
                    }
                    onPlay?.Invoke(playable);
                }
                else
                {
                    Object.Destroy(o);
                }
            }));

            return playable.Id;
        }

        public static uint Play(ActObject actObj, GameObject actor, Action<bool> onFinish)
        {
            return Play(actObj, 0f, actor, onFinish);
        }
        
        public static uint Play(ActObject actObj, float startTime, GameObject actor, Action<bool> onFinish)
        {
            if (actObj == null)
                return 0;

            var playable = new ActPlayable()
            {
                Id = ++s_idCounter,
                Name = actObj.name,
                Actor = actor,
                ActObj = actObj,
                BeginTime = Time.time,
                BeginOffset = startTime,
                OnFinishCallback = onFinish,
            };
            GetCurrentPlayables().Add(playable.Id, playable);

            foreach (var act in actObj.ActList)
            {
                ActBase.InternalCreated(act, actObj, actor);
                if (act != null && act.StartTime == 0)
                    ActBase.InternalStart(act, playable.BeginOffset);
            }

            return playable.Id;
        }

        public static void Pause(uint actId)
        {
            var playable = GetPlayable(actId);
            if (playable == null)
                return;

            if (playable.IsPaused)
                return;

            playable.Pause();

            var actObj = playable.ActObj;
            if (actObj != null)
            {
                foreach (var act in actObj.ActList)
                    ActBase.InternalPause(act);
            }
        }

        public static void Resume(uint actId)
        {
            var playable = GetPlayable(actId);
            if (playable == null)
                return;

            if (!playable.IsPaused)
                return;

            playable.Resume();

            var actObj = playable.ActObj;
            if (actObj != null)
            {
                foreach (var act in actObj.ActList)
                    ActBase.InternalResume(act);
            }
        }

        public static void Stop(uint actId, bool isDone = false)
        {
            var playable = GetPlayable(actId);
            if (playable == null)
                return;

            var actObj = playable.ActObj;
            if (actObj)
            {
                foreach (var act in actObj.ActList)
                {
                    if (act != null)
                    {
                        if (!act.Finished)
                        {
                            ActBase.InternalFinish(act);
                        }

                        ActBase.InternalDestroyed(act);
                    }
                }
            }

            s_remPlayables.Add(actId, playable);
            playable.OnFinishCallback?.Invoke(isDone);
        }

        static void Update()
        {
            if (s_ActPlayables.Count == 0)
                return;
            
            // 处理更新逻辑
            s_isUpdating = true;
            {
                DoUpdate();
            }
            s_isUpdating = false;

            // 处理添加逻辑
            foreach (var (id, playable) in s_addPlayables)
            {
                s_ActPlayables.Add(id, playable);
            }

            s_addPlayables.Clear();

            // 处理删除逻辑
            foreach (var (id, playable) in s_remPlayables)
            {
                s_ActPlayables.Remove(id);
            }

            s_remPlayables.Clear();
        }

        private static void DoUpdate()
        {
            foreach (var (actId, handle) in s_ActPlayables)
            {
                if (!handle.IsValid)
                    continue; // 还没有加载完跳过
                if (s_remPlayables.ContainsKey(actId))
                    continue; // 已经删掉了跳过
                if (handle.IsPaused)
                    continue; // 暂停中跳过
                
                var begin = handle.BeginTime;
                var done = begin + handle.ActObj.Duration;

                float curTime = Time.time + handle.BeginOffset;

                var actList = handle.ActObj.ActList;
                foreach (var act in actList)
                {
                    if (act != null)
                    {
                        var startTime = begin + act.StartTime;
                        var endTime = startTime + Mathf.Max(0f, act.Duration);

                        if (!act.Started && curTime >= startTime)
                        {
                            
                            ActBase.InternalStart(act, handle.BeginOffset);
                        }

                        if (!act.Finished && curTime >= endTime)
                        {
                            ActBase.InternalFinish(act);
                        }

                        ActBase.InternalUpdate(act, curTime);
                    }
                }

                // 区间循环回卷（适配本类的绝对时间模型）：
                // 到达 [LoopStart, LoopEnd) 终点时平移 BeginTime，使局部时间轴跳回循环起点。
                // 前置区间（循环起点前已结束）的节点只播一次保持完成；
                // 循环区间内的节点 Reset 后按回卷局部时间的偏移量重启。
                if (handle.ActObj.TryGetSectionLoop(out float ls, out float le) && curTime >= begin + le)
                {
                    float span = le - ls;
                    // 取模处理大步长（低帧率/短循环区间）一次跨多圈
                    float local = curTime - begin;                 // 当前局部时间轴位置
                    float wrappedLocal = ls + (local - ls) % span;

                    // 平移基准：回卷后局部时间 == wrappedLocal
                    // （局部时间 = curTime - BeginTime，与加载时 begin=Time.time、
                    //   整段循环 BeginTime=Time.time 的既有公式一致；Pause/Resume 的
                    //   BeginTime 差值补偿不受平移影响）
                    handle.BeginTime = curTime - wrappedLocal;

                    foreach (var act in actList)
                    {
                        if (act == null)
                            continue;

                        float end = act.StartTime + Mathf.Max(0f, act.Duration);
                        if (end <= ls)
                            continue; // 前置区间节点：保持已完成

                        ActBase.InternalReset(act);
                        if (act.StartTime <= wrappedLocal)
                        {
                            ActBase.InternalStart(act, wrappedLocal - act.StartTime);
                            // OnSeekTo 内部按 curTime - StartTime 计算节点局部时间，
                            // 必须传 ActObject 局部时间，不能传绝对时间
                            ActBase.InternalSeekTo(act, wrappedLocal);
                            ActBase.InternalUpdate(act, curTime); // OnUpdate 沿用本 Director 绝对时间的存量口径
                        }
                        // StartTime > wrappedLocal 的节点由后续帧 !Started && curTime >= begin'+startTime 自然启动
                    }

                    continue; // 跳过本帧的整段循环/结束判定（LoopEnd==Duration 时区间循环优先）
                }

                if (curTime >= done)
                {
                    if (!handle.ActObj.Loop)
                    {
                        Stop(actId, true);
                    }
                    else
                    {
                        foreach (var act in actList)
                        {
                            handle.BeginTime = Time.time;
                            if (act != null)
                            {
                                ActBase.InternalReset(act);
                                if (act.StartTime == 0)
                                {
                                    ActBase.InternalStart(act, handle.BeginOffset);
                                    ActBase.InternalUpdate(act, curTime);
                                }
                            }
                        }
                    }
                }
            }
        }
        
        
    }
} // namespace Umbreey