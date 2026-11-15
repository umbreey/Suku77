using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace Umbreey
{
    [CreateAssetMenu(menuName ="Umbreey/Act")]
    public class ActObject : ScriptableObject
    {
        [SerializeReference]
        public List<ActBase> ActList = new List<ActBase>();
        
        [LabelText("循环播放")]
        public bool Loop;
        
        [LabelText("区间循环")]
        public bool SectionLoop;

        [LabelText("循环起点")]
        [Clamp(0, float.MaxValue)]
        public float LoopStartTime;

        [LabelText("循环终点")]
        [Clamp(0, float.MaxValue)]
        public float LoopEndTime;

        [LabelText("总时间",false)]
        public float Duration;

        [LabelText("描述")]
        public string Desc;

        public Blackboard Blackboard { get; private set; }

        /// <summary>
        /// Epoch 计数器，用于 ActProgressDirector 后退寻址时使过期的异步加载回调失效。
        /// 旧 ActDirector 不使用此字段，默认为 0。
        /// </summary>
        [HideInInspector]
        public uint CurrentEpoch;

        /// <summary>
        /// 是否处于进度拖拽（Scrub）模式。
        /// 拖拽期间各节点的 Finish 走"隐藏"路径而非"销毁+消散"，实例保留供后退寻址复用。
        /// 由 ActProgressDirector 的 BeginScrub/EndScrub 维护。
        /// </summary>
        public bool IsScrubbing { get; set; }

        public void SetBlackboard(Blackboard blackboard)
        {
            Blackboard = blackboard;
        }

        /// <summary>
        /// 获取有效的区间循环参数 [start, end)。
        /// 约定：LoopEndTime &lt;= 0 时视为循环到总时长末尾；配置超出总时长时自动收紧到总时长。
        /// 若未开启区间循环或参数非法（起点为负 / 终点不大于起点），返回 false。
        /// </summary>
        public bool TryGetSectionLoop(out float start, out float end)
        {
            start = 0f;
            end = 0f;

            if (!SectionLoop)
                return false;

            start = LoopStartTime;
            end = LoopEndTime <= 0f ? Duration : Mathf.Min(LoopEndTime, Duration);
            if (LoopStartTime < 0f || end <= LoopStartTime)
                return false;

            return true;
        }
    }
}
