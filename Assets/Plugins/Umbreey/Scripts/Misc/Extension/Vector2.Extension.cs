using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static class Vector2Extension
{
    public static bool Approximately(this Vector2 self, Vector2 target, float tolerance = Vector3Extension.KFloatEpsilon)
    {
        if (Mathf.Abs(self.x - target.x) > tolerance)
            return false;
        if (Mathf.Abs(self.y - target.y) > tolerance)
            return false;
        return true;
    }

    public static bool IsInValid(this Vector2 self)
    {
        return Single.IsInfinity(self.x) || Single.IsInfinity(self.y) || Single.IsNaN(self.x) || Single.IsNaN(self.y);
    }
}    



