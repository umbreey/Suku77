using System;
using System.Collections;
using UnityEngine;
using Umbreey;

namespace Scripts
{
    /// <summary>
    /// 渠道闪屏占位。HybridUtils 会在进入热更前等待其结束；未挂载则跳过。
    /// </summary>
    public class ChannelSplash : MonoBehaviour
    {
        [SerializeField] float m_MinSeconds;

        public IEnumerator WaitFinish()
        {
            if (m_MinSeconds > 0f)
                yield return new WaitForSecondsRealtime(m_MinSeconds);
        }
    }
}
