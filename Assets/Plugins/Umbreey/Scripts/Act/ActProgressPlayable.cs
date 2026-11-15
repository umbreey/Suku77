using System;
using UnityEngine;

namespace Umbreey
{
    /// <summary>
    /// 虚拟时间驱动的 Playable，支持进度双向同步。
    /// 与 <see cref="ActPlayable"/> 的区别：用 CurrentTime 替代 Time.time，
    /// 支持外部读取/设置播放进度。
    /// </summary>
    public class ActProgressPlayable
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

        public Action<bool> OnFinishCallback;

        /// <summary>
        /// 虚拟播放时间，由 Director 每帧推进，不再依赖 Time.time。
        /// </summary>
        public float CurrentTime { get; private set; }

        /// <summary>
        /// 播放进度 0~1。
        /// </summary>
        public float Progress
        {
            get
            {
                if (ActObj == null || ActObj.Duration <= 0) return 0f;
                return Mathf.Clamp01(CurrentTime / ActObj.Duration);
            }
        }

        /// <summary>
        /// 播放进度 1~100（整数，直接给 UI 用）。
        /// </summary>
        public int ProgressInt => Mathf.RoundToInt(Progress * 100f);

        /// <summary>
        /// 外部 Pause 标记（由 Pause/Resume 控制，不受 Scrub 影响）。
        /// </summary>
        public bool IsExternallyPaused { get; private set; }

        /// <summary>
        /// 有效暂停状态：外部 Pause 或 Scrub Pause 任一为 true。
        /// </summary>
        public bool IsPaused => IsExternallyPaused || ScrubPaused;

        public bool IsValid { get; private set; }

        /// <summary>
        /// 是否已播放完成（非循环 Act 播放到结尾时自动 Pause 并置此标记）。
        /// 此状态下：实例保留、时间停在 Duration、IsPaused=true，
        /// BeginScrub/SetProgress 可寻址但不会 Resume，Resume 恢复播放，Stop 销毁。
        /// </summary>
        public bool IsFinished { get; set; }

        /// <summary>
        /// 是否处于进度拖拽（Scrub）模式。
        /// 拖拽期间冻结时间推进，仅按外部进度寻址；后退寻址走"隐藏+重置"而非销毁重建。
        /// </summary>
        public bool IsScrubbing { get; set; }

        /// <summary>
        /// Scrub 主动触发的暂停标记。
        /// 与 IsExternallyPaused 独立：外部 Resume 不影响此标记，仅 EndScrub 清除。
        /// 有效暂停 IsPaused = IsExternallyPaused || ScrubPaused。
        /// </summary>
        public bool ScrubPaused { get; set; }

        /// <summary>
        /// 是否有待处理的寻址请求（本帧内已调用 SetProgress 但尚未在 Update 中执行）。
        /// 为 true 时，DoUpdate 会跳过时间推进，避免与待执行的寻址冲突。
        /// </summary>
        public bool HasPendingSeek { get; set; }

        /// <summary>
        /// 待寻址的目标时间。
        /// </summary>
        public float PendingSeekTime { get; set; }

        /// <summary>
        /// Epoch 计数器，后退寻址时递增，用于丢弃过期的异步加载回调。
        /// </summary>
        public uint Epoch { get; private set; }

        private void CacheIsValid()
        {
            IsValid = ActObj != null && Actor != null;
        }

        /// <summary>
        /// 推进虚拟时间（由 Director 每帧调用）。
        /// </summary>
        public void AdvanceTime(float delta)
        {
            if (!IsPaused)
                CurrentTime += delta;
        }

        /// <summary>
        /// 直接设置时间（寻址用）。
        /// </summary>
        public void SetTime(float time)
        {
            float duration = ActObj != null ? ActObj.Duration : 0f;
            CurrentTime = Mathf.Clamp(time, 0f, Mathf.Max(0f, duration));
        }

        /// <summary>
        /// 重置到起点（不销毁节点，仅清零时间）。
        /// </summary>
        public void ResetTime()
        {
            CurrentTime = 0f;
        }

        public void Pause()
        {
            IsExternallyPaused = true;
        }

        public void Resume()
        {
            IsExternallyPaused = false;
        }

        /// <summary>
        /// 递增 Epoch，使所有基于旧 Epoch 的异步回调失效。
        /// </summary>
        public void IncrementEpoch()
        {
            Epoch++;
            if (ActObj != null)
                ActObj.CurrentEpoch = Epoch;
        }
    }
}
