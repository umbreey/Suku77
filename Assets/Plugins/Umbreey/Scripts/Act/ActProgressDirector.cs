using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Umbreey
{
    /// <summary>
    /// 带进度控制的 Act 时间轴调度器。
    /// 与 <see cref="ActDirector"/> 并行存在，互不干扰。
    /// 支持通过外部进度值(0~1 或 1~100)控制播放，并实时输出当前播放进度。
    /// </summary>
    public static class ActProgressDirector
    {
        private static Dictionary<uint, ActProgressPlayable> s_ActPlayables = new Dictionary<uint, ActProgressPlayable>();
        private static Dictionary<uint, ActProgressPlayable> s_AddPlayables = new Dictionary<uint, ActProgressPlayable>();
        private static Dictionary<uint, ActProgressPlayable> s_RemPlayables = new Dictionary<uint, ActProgressPlayable>();
        private static List<ActProgressPlayable> s_SeekBuffer = new List<ActProgressPlayable>();
        private static bool s_IsUpdating;
        private static uint s_IdCounter;

        static ActProgressDirector()
        {
            TaskManager.Update(Update);
        }

        private static TaskManager.Timer __OnTimer;
        private static TaskManager.Timer m_OnTimer
        {
            get
            {
                if (__OnTimer == null)
                    __OnTimer = new TaskManager.Timer();
                return __OnTimer;
            }
        }

        public static void DestroyTimeout(Object obj, float timeout)
        {
            if (obj == null)
                return;

            m_OnTimer.Timeout(() =>
            {
                if (obj)
                    Object.Destroy(obj);
            }, timeout);
        }

        public static bool IsValid(uint actId)
        {
            var playable = GetPlayable(actId);
            return playable is { IsValid: true };
        }

        /// <summary>
        /// 当前 Act 是否已播放完成（播到结尾后自动 Pause，实例保留）。
        /// </summary>
        public static bool IsCompleted(uint actId)
        {
            return GetPlayable(actId)?.IsFinished ?? false;
        }

        /// <summary>
        /// 当前 Act 是否处于暂停状态（含播放完成后自动 Pause）。
        /// </summary>
        public static bool IsPaused(uint actId)
        {
            return GetPlayable(actId)?.IsPaused ?? false;
        }

        public static ActProgressPlayable GetPlayable(uint actId)
        {
            if (s_RemPlayables.ContainsKey(actId))
                return null;

            if (s_AddPlayables.TryGetValue(actId, out var addPlayable))
                return addPlayable;

            s_ActPlayables.TryGetValue(actId, out var playable);
            return playable;
        }

        private static Dictionary<uint, ActProgressPlayable> GetCurrentPlayables()
        {
            return s_IsUpdating ? s_AddPlayables : s_ActPlayables;
        }

        #region Play

        public static uint Play(string assetName, GameObject actor, bool needLoop = false)
        {
            return Play(assetName, 0f, actor, null, null, needLoop);
        }

        public static uint Play(string assetName, GameObject actor, Action<bool> onFinish)
        {
            return Play(assetName, 0f, actor, null, onFinish, false);
        }

        public static uint Play(string assetName, GameObject actor, Action<ActProgressPlayable> onPlay, Action<bool> onFinish)
        {
            return Play(assetName, 0f, actor, onPlay, onFinish, false);
        }

        public static uint Play(string assetName, float startTime, GameObject actor, Action<bool> onFinish)
        {
            return Play(assetName, startTime, actor, null, onFinish, false);
        }

        /// <summary>
        /// 异步加载 ActObject 资源并创建播放实例。
        /// </summary>
        /// <param name="assetName">资源名（不含扩展名）</param>
        /// <param name="startTime">起始播放时间（虚拟时间）</param>
        /// <param name="actor">执行者</param>
        /// <param name="onPlay">加载完成回调</param>
        /// <param name="onFinish">播放结束回调</param>
        /// <param name="needLoop">是否循环播放</param>
        public static uint Play(string assetName, float startTime, GameObject actor,
            Action<ActProgressPlayable> onPlay, Action<bool> onFinish, bool needLoop = false)
        {
            if (string.IsNullOrEmpty(assetName))
                return 0;

            var playable = new ActProgressPlayable
            {
                Id = ++s_IdCounter,
                Name = assetName,
                Actor = actor,
                OnFinishCallback = onFinish,
            };
            GetCurrentPlayables().Add(playable.Id, playable);

            Assets.InstantiateAsync<ActObject>($"{assetName}.asset", o =>
            {
                if (needLoop)
                    o.Loop = true;

                if (GetPlayable(playable.Id) != null)
                {
                    playable.ActObj = o;
                    playable.SetTime(startTime);

                    // 同步 Epoch 到 ActObject
                    playable.IncrementEpoch();

                    foreach (var act in o.ActList)
                        ActBase.InternalCreated(act, o, actor);

                    // 寻址到 startTime（触发已完成节点的 Start/Finish）
                    if (startTime > 0f)
                        SeekToTime(playable, startTime);

                    onPlay?.Invoke(playable);
                }
                else
                {
                    Object.Destroy(o);
                }
            });

            return playable.Id;
        }

        /// <summary>
        /// 直接使用已有 ActObject 播放（同步，无异步加载）。
        /// </summary>
        public static uint Play(ActObject actObj, GameObject actor, Action<bool> onFinish = null)
        {
            return Play(actObj, 0f, actor, onFinish);
        }

        /// <summary>
        /// 直接使用已有 ActObject 播放，从指定时间开始。
        /// </summary>
        public static uint Play(ActObject actObj, float startTime, GameObject actor, Action<bool> onFinish = null)
        {
            if (actObj == null)
                return 0;

            var playable = new ActProgressPlayable
            {
                Id = ++s_IdCounter,
                Name = actObj.name,
                Actor = actor,
                ActObj = actObj,
                OnFinishCallback = onFinish,
            };
            playable.SetTime(startTime);
            playable.IncrementEpoch();
            GetCurrentPlayables().Add(playable.Id, playable);

            foreach (var act in actObj.ActList)
                ActBase.InternalCreated(act, actObj, actor);

            if (startTime > 0f)
                SeekToTime(playable, startTime);

            return playable.Id;
        }

        #endregion

        #region Progress API

        /// <summary>
        /// 获取当前播放进度 (0~1)。
        /// </summary>
        public static float GetProgress(uint actId)
        {
            return GetPlayable(actId)?.Progress ?? 0f;
        }

        /// <summary>
        /// 获取当前播放进度 (1~100 整数)。
        /// </summary>
        public static int GetProgressInt(uint actId)
        {
            return GetPlayable(actId)?.ProgressInt ?? 0;
        }

        /// <summary>
        /// 通过外部进度值控制播放位置。
        /// 采用延迟执行：多次调用只保留最后一次，在下一帧 Update 时统一处理。
        /// 适用于进度条拖拽等高频调用场景。
        /// </summary>
        /// <param name="actId">播放 ID</param>
        /// <param name="progress01">进度 0~1</param>
        public static void SetProgress(uint actId, float progress01)
        {
            var playable = GetPlayable(actId);
            if (playable == null || !playable.IsValid)
                return;

            progress01 = Mathf.Clamp01(progress01);
            float targetTime = progress01 * playable.ActObj.Duration;

            // 延迟执行：记录待寻址目标，下一帧 Update 时统一处理
            playable.HasPendingSeek = true;
            playable.PendingSeekTime = targetTime;
        }

        /// <summary>
        /// 立即执行寻址（不延迟到下一帧）。
        /// 仅用于需要同步寻址的场景，如 Play 时指定起始时间。
        /// </summary>
        public static void SetProgressImmediate(uint actId, float progress01)
        {
            var playable = GetPlayable(actId);
            if (playable == null || !playable.IsValid)
                return;

            progress01 = Mathf.Clamp01(progress01);
            float targetTime = progress01 * playable.ActObj.Duration;
            SeekToTime(playable, targetTime);
        }

        /// <summary>
        /// 通过外部进度值控制播放位置（1~100 整数）。
        /// </summary>
        public static void SetProgressInt(uint actId, int progress1100)
        {
            SetProgress(actId, Mathf.Clamp01(progress1100 / 100f));
        }

        #endregion

        #region Time / Frame Seek

        /// <summary>
        /// 跳转到指定时间点（秒），延迟到下一帧 Update 时执行。
        /// 适用于高频调用场景（如帧步进按钮连续点击）。
        /// </summary>
        /// <param name="actId">播放 ID</param>
        /// <param name="time">目标时间（秒，会自动 Clamp 到 [0, Duration]）</param>
        public static void SetTime(uint actId, float time)
        {
            var playable = GetPlayable(actId);
            if (playable == null || !playable.IsValid)
                return;

            float duration = playable.ActObj.Duration;
            time = Mathf.Clamp(time, 0f, Mathf.Max(0f, duration));

            playable.HasPendingSeek = true;
            playable.PendingSeekTime = time;
        }

        /// <summary>
        /// 跳转到指定时间点（秒），立即执行寻址（不等下一帧）。
        /// </summary>
        public static void SetTimeImmediate(uint actId, float time)
        {
            var playable = GetPlayable(actId);
            if (playable == null || !playable.IsValid)
                return;

            SeekToTime(playable, time);
        }

        /// <summary>
        /// 跳转到指定帧，延迟到下一帧 Update 时执行。
        /// </summary>
        /// <param name="actId">播放 ID</param>
        /// <param name="frame">目标帧（0 起算）</param>
        /// <param name="frameRate">帧率，默认 30</param>
        public static void SetFrame(uint actId, int frame, float frameRate = 30f)
        {
            if (frameRate <= 0f)
                return;
            SetTime(actId, frame / frameRate);
        }

        /// <summary>
        /// 跳转到指定帧，立即执行寻址（不等下一帧）。
        /// </summary>
        public static void SetFrameImmediate(uint actId, int frame, float frameRate = 30f)
        {
            if (frameRate <= 0f)
                return;
            SetTimeImmediate(actId, frame / frameRate);
        }

        #endregion

        #region Seek

        /// <summary>
        /// 寻址到指定时间点。
        /// 前进：评估中间节点的 Start/Update/Finish。
        /// 后退：SupportScrub 节点走"隐藏+重置"（实例保留复用），
        ///       其余节点走"销毁重建"兜底路径，然后从零评估到目标时间。
        /// </summary>
        private static void SeekToTime(ActProgressPlayable playable, float targetTime)
        {
            if (playable?.ActObj == null)
                return;

            var actList = playable.ActObj.ActList;
            float prevTime = playable.CurrentTime;
            float duration = playable.ActObj.Duration;
            targetTime = Mathf.Clamp(targetTime, 0f, Mathf.Max(0f, duration));

            // 小增量跳过：避免微小进度变化触发全量评估
            if (Mathf.Approximately(targetTime, prevTime))
            {
                playable.SetTime(targetTime);
                return;
            }

            if (targetTime < prevTime)
            {
                // ===== 后退寻址 =====
                // Epoch 仍需递增：销毁重建的兜底节点可能有在途异步加载，旧回调必须失效
                playable.IncrementEpoch();

                foreach (var act in actList)
                {
                    if (act == null)
                        continue;

                    if (act.Started && !act.Finished)
                        ActBase.InternalFinish(act);

                    if (act.SupportScrub)
                    {
                        // Scrub 节点：隐藏实例 + 重置状态，不销毁（下次前进命中时复用）
                        ActBase.InternalSeekReset(act);
                        ActBase.InternalReset(act);
                    }
                    else
                    {
                        // 兜底路径：销毁重建
                        ActBase.InternalDestroyed(act);
                        ActBase.InternalCreated(act, playable.ActObj, playable.Actor);
                    }
                }

                prevTime = 0f;
            }

            // ===== 前进评估（从 prevTime 到 targetTime）=====
            foreach (var act in actList)
            {
                if (act == null)
                    continue;

                float start = act.StartTime;
                float end = start + Mathf.Max(0f, act.Duration);

                if (targetTime >= end)
                {
                    // 节点应已完成
                    if (!act.Started)
                        ActBase.InternalStart(act, 0f);
                    if (!act.Finished)
                        ActBase.InternalFinish(act);
                }
                else if (targetTime >= start)
                {
                    // 节点应处于活跃状态
                    if (!act.Started)
                        ActBase.InternalStart(act, targetTime - start);
                    // 寻址跳转：通知节点将外部资源（动画帧/粒子等）对齐到目标时间，
                    // 否则普通 seek 只推进虚拟时间、动画仍停留在跳变前的位置
                    ActBase.InternalSeekTo(act, targetTime);
                    ActBase.InternalUpdate(act, targetTime);
                }
                // targetTime < start: 节点未开始，跳过
            }

            playable.SetTime(targetTime);

            // 更新 IsFinished 标记：寻址到结尾视为已完成，离开结尾则清除
            playable.IsFinished = targetTime >= duration;
        }

        #endregion

        #region Scrub（进度拖拽）

        /// <summary>
        /// 进入进度拖拽模式。
        /// 始终设置 ScrubPaused 标记；若 Act 正在播放（有效暂停=false），则 Pause 节点。
        /// 外部 Pause 状态不受影响，两个 Pause 源独立运作。
        /// 典型用法：进度条 onPointerDown 时调用，配合 onDrag 中的 SetProgress。
        /// </summary>
        public static void BeginScrub(uint actId)
        {
            var playable = GetPlayable(actId);
            if (playable == null || !playable.IsValid)
                return;

            bool wasPaused = playable.IsPaused;
            playable.ScrubPaused = true;
            playable.IsScrubbing = true;
            if (playable.ActObj != null)
                playable.ActObj.IsScrubbing = true;

            // 从播放状态进入暂停时，通知节点 OnPause
            if (!wasPaused)
            {
                var actObj = playable.ActObj;
                if (actObj != null)
                {
                    foreach (var act in actObj.ActList)
                        ActBase.InternalPause(act);
                }
            }
        }

        /// <summary>
        /// 退出进度拖拽模式。
        /// 清除 ScrubPaused；若外部未 Pause 则恢复节点播放，否则保持暂停。
        /// 典型用法：进度条 onPointerUp 时调用。
        /// </summary>
        public static void EndScrub(uint actId)
        {
            var playable = GetPlayable(actId);
            if (playable == null || !playable.IsValid)
                return;

            playable.IsScrubbing = false;
            if (playable.ActObj != null)
                playable.ActObj.IsScrubbing = false;

            if (playable.ScrubPaused)
            {
                playable.ScrubPaused = false;

                // 外部未 Pause 时恢复节点播放
                if (!playable.IsExternallyPaused)
                {
                    playable.IsFinished = false;

                    var actObj = playable.ActObj;
                    if (actObj != null)
                    {
                        foreach (var act in actObj.ActList)
                            ActBase.InternalResume(act);
                    }
                }
            }
        }

        #endregion

        #region Pause / Resume / Stop

        public static void Pause(uint actId)
        {
            var playable = GetPlayable(actId);
            if (playable == null || !playable.IsValid)
                return;

            bool wasPaused = playable.IsPaused;
            playable.Pause(); // 设置外部暂停标记（不影响 ScrubPaused）

            // 从播放状态进入暂停时，通知节点 OnPause
            if (!wasPaused)
            {
                var actObj = playable.ActObj;
                if (actObj != null)
                {
                    foreach (var act in actObj.ActList)
                        ActBase.InternalPause(act);
                }
            }
        }

        public static void Resume(uint actId)
        {
            var playable = GetPlayable(actId);
            if (playable == null || !playable.IsValid)
                return;

            if (!playable.IsExternallyPaused)
                return;

            // 清除外部暂停标记（不影响 ScrubPaused）
            playable.IsFinished = false;
            playable.Resume();

            // Scrub 仍在进行时，节点保持暂停
            if (!playable.ScrubPaused)
            {
                var actObj = playable.ActObj;
                if (actObj != null)
                {
                    foreach (var act in actObj.ActList)
                        ActBase.InternalResume(act);
                }
            }
        }

        public static void Stop(uint actId, bool isDone = false)
        {
            var playable = GetPlayable(actId);
            if (playable == null)
                return;

            // 已播放完成时回调已在自动 Pause 时调用过，不重复
            bool wasFinished = playable.IsFinished;
            playable.IsFinished = false;

            // 清理 Scrub 标记（ActObject 是共享资源，避免标记泄漏到后续播放）
            playable.IsScrubbing = false;
            playable.ScrubPaused = false;
            if (playable.ActObj != null)
                playable.ActObj.IsScrubbing = false;

            var actObj = playable.ActObj;
            if (actObj != null)
            {
                foreach (var act in actObj.ActList)
                {
                    if (act == null)
                        continue;

                    if (!act.Finished)
                        ActBase.InternalFinish(act);

                    ActBase.InternalDestroyed(act);
                }
            }

            s_RemPlayables.Add(actId, playable);

            if (!wasFinished)
                playable.OnFinishCallback?.Invoke(isDone);
        }

        #endregion

        #region Update

        private static void Update()
        {
            if (s_ActPlayables.Count == 0 && s_AddPlayables.Count == 0)
                return;

            s_IsUpdating = true;
            {
                // 优先处理待寻址请求（每帧只执行最后一次 SetProgress）
                ProcessPendingSeeks();
                DoUpdate();
            }
            s_IsUpdating = false;

            // 处理添加
            foreach (var (id, playable) in s_AddPlayables)
                s_ActPlayables.Add(id, playable);
            s_AddPlayables.Clear();

            // 处理删除
            foreach (var (id, playable) in s_RemPlayables)
                s_ActPlayables.Remove(id);
            s_RemPlayables.Clear();
        }

        /// <summary>
        /// 处理所有待寻址请求。每帧调用一次，确保高频 SetProgress 只执行最后一次。
        /// 先快照待处理列表再执行，避免 SeekToTime 回调中新增 Playable 导致枚举异常。
        /// </summary>
        private static void ProcessPendingSeeks()
        {
            // 快照待寻址的 Playable（SeekToTime 回调可能修改字典）
            s_SeekBuffer.Clear();

            foreach (var (id, playable) in s_ActPlayables)
            {
                if (!playable.IsValid || s_RemPlayables.ContainsKey(id))
                    continue;
                if (playable.HasPendingSeek)
                    s_SeekBuffer.Add(playable);
            }

            foreach (var (id, playable) in s_AddPlayables)
            {
                if (!playable.IsValid)
                    continue;
                if (playable.HasPendingSeek)
                    s_SeekBuffer.Add(playable);
            }

            // 统一执行寻址
            foreach (var playable in s_SeekBuffer)
            {
                if (!playable.HasPendingSeek)
                    continue; // 可能已被前面的 SeekToTime 间接触发

                // 重新校验是否仍在播放（可能被前面的回调 Stop 掉）
                if (GetPlayable(playable.Id) != playable)
                    continue;

                playable.HasPendingSeek = false;
                SeekToTime(playable, playable.PendingSeekTime);
            }
        }

        private static void DoUpdate()
        {
            foreach (var (actId, handle) in s_ActPlayables)
            {
                if (!handle.IsValid)
                    continue;
                if (s_RemPlayables.ContainsKey(actId))
                    continue;
                // 有待处理寻址时跳过本帧时间推进（寻址已在 ProcessPendingSeeks 中处理）
                if (handle.HasPendingSeek)
                    continue;

                // Scrub 模式：即使 Paused 也需驱动节点 OnUpdate 重采样媒体（粒子/动画）。
                // 拖住不动时 Act 停在当前帧，不推进时间。
                if (handle.IsScrubbing)
                {
                    float frozenTime = handle.CurrentTime;
                    foreach (var act in handle.ActObj.ActList)
                    {
                        if (act == null)
                            continue;
                        ActBase.InternalUpdate(act, frozenTime);
                    }
                    continue;
                }

                // Paused（含播放完成自动 Pause）：冻结，不推进时间
                if (handle.IsPaused)
                    continue;

                // 虚拟时间推进
                handle.AdvanceTime(Time.deltaTime);
                float curTime = handle.CurrentTime;

                var actList = handle.ActObj.ActList;
                foreach (var act in actList)
                {
                    if (act == null)
                        continue;

                    float start = act.StartTime;
                    float end = start + Mathf.Max(0f, act.Duration);
                    
                    if (!act.Started && curTime >= start)
                        ActBase.InternalStart(act, 0f);

                    if (!act.Finished && curTime >= end)
                        ActBase.InternalFinish(act);

                    ActBase.InternalUpdate(act, curTime);
                }

                // 区间循环回卷：播放到 [LoopStart, LoopEnd) 终点时，回卷到循环起点。
                // 前置区间（循环起点前已结束）的节点只播一次保持完成；
                // 循环区间内的节点 Reset 后按回卷时间的偏移量重启。
                if (handle.ActObj.TryGetSectionLoop(out float ls, out float le) && curTime >= le)
                {
                    float span = le - ls;
                    // 取模处理大步长（低帧率/短循环区间）一次跨多圈
                    float wrapped = ls + (curTime - ls) % span;

                    foreach (var act in actList)
                    {
                        if (act == null)
                            continue;

                        float end = act.StartTime + Mathf.Max(0f, act.Duration);
                        if (end <= ls)
                            continue; // 前置区间节点：保持已完成

                        ActBase.InternalReset(act);
                        if (act.StartTime <= wrapped)
                        {
                            ActBase.InternalStart(act, wrapped - act.StartTime);
                            // 对齐外部资源（动画/粒子）到回卷后时间：
                            // OnStart 可能被节点内部"循环动画已播放"短路跳过重定位，
                            // OnSeekTo 提供非 Scrub 寻址对齐，保证进度与内容同步
                            ActBase.InternalSeekTo(act, wrapped);
                            ActBase.InternalUpdate(act, wrapped);
                        }
                        // StartTime > wrapped 的节点由后续帧 !Started && curTime >= start 判定自然启动
                    }

                    handle.SetTime(wrapped);
                    continue;
                }

                // 播放结束
                if (curTime >= handle.ActObj.Duration)
                {
                    if (!handle.ActObj.Loop)
                    {
                        // 播放完成：保留实例，自动 Pause，等待 Resume/BeginScrub/Stop
                        if (!handle.IsFinished)
                        {
                            handle.IsFinished = true;
                            handle.Pause();
                            foreach (var act in actList)
                            {
                                if (act == null)
                                    continue;
                                ActBase.InternalPause(act);
                            }
                            handle.OnFinishCallback?.Invoke(true);
                        }
                    }
                    else
                    {
                        // 循环：重置时间和节点状态
                        handle.ResetTime();
                        foreach (var act in actList)
                        {
                            if (act == null)
                                continue;

                            ActBase.InternalReset(act);
                            if (act.StartTime == 0)
                            {
                                ActBase.InternalStart(act, 0f);
                                ActBase.InternalUpdate(act, 0f);
                            }
                        }
                    }
                }
            }
        }

        #endregion
    }
}
