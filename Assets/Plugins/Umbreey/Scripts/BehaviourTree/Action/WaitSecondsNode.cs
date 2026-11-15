using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Umbreey
{
    public class WaitSecondsNode : ActionNode
    {
        [SerializeReference]
        protected BBoardVarFloat WaitTime = new BBoardVarFloat(null);
        
        [SerializeReference] 
        protected BBoardVarFloat Variance = new BBoardVarFloat(null);
        
        protected override void OnStart()
        {
            float time = Math.Max(0, WaitTime.Value + Random.Range(-Variance.Value, Variance.Value));
            m_OnTimer.Timeout(OnTimeout, time);
        }

        private void OnTimeout()
        {
            Finish(true);
        }
    }
}
