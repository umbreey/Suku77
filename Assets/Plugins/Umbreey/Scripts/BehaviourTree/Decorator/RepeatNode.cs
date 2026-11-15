using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Umbreey
{
    public class RepeatNode : DecoratorNode
    {
        [SerializeReference]
        protected BBoardVarInt LoopCount = new BBoardVarInt(null);

        private int _currentIdx;
        
        protected override void OnChildFinish(NodeBase child, bool success)
        {
            _currentIdx++;
            
            int maxIdx = LoopCount.Value;
            if (maxIdx <= 0 || _currentIdx < maxIdx)
            {
                m_OnTimer.NextFrame(() =>
                {
                    child.Start();
                });
            }
            else
            {
                Finish(true);
            }
        }
        
    }
}
