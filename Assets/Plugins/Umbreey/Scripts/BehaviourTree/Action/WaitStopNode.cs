using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Umbreey
{
    public class WaitStopNode : ActionNode
    {
        [SerializeReference]
        protected BBoardVarBool StopAsSuccess = new BBoardVarBool(null);

        protected override void OnStart()
        {
            
        }

        protected override void OnStop()
        {
            Finish(StopAsSuccess.Value);
        }
    }
}
