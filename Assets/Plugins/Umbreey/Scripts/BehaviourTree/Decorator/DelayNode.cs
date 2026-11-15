using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Umbreey
{
    public class DelayNode : DecoratorNode
    {
        [SerializeReference] 
        protected BBoardVarFloat WaitTime = new BBoardVarFloat(null);
        
        [SerializeReference] 
        protected BBoardVarFloat Variance = new BBoardVarFloat(null);

        protected override void OnStart()
        {
            float waitTime = WaitTime.Value + Random.Range(-Variance.Value, Variance.Value);
            m_OnTimer.Timeout(() =>
            {
                base.OnStart();
            }, waitTime);
        }
        
    }
}
