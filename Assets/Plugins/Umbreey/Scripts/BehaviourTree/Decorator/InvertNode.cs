using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Umbreey
{
    public class InvertNode : DecoratorNode    
    {
        protected override void OnChildFinish(NodeBase child, bool success)
        {
            Finish(!success);
        }
    }
}
