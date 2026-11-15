using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Umbreey
{
    public class RandomNode : DecoratorNode
    {
        [Range(0, 1)]
        [SerializeReference]
        public BBoardVarFloat Probability = new BBoardVarFloat(null);

        protected override void OnStart()
        {
            base.OnStart();
            
            if (Random.value <= Probability.Value)
            {
                Child.Start();
            }
            else
            {
                Finish(false);
            }
        }
    }
}
