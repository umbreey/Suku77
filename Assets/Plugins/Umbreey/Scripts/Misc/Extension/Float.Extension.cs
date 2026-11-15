using System;
using UnityEngine;

public static class FloatExtension
{
    public static bool Approximately(this float self, float target, float tolerance = 0.001f)
    {
        if (Mathf.Abs(self - target) > tolerance)
            return false;
        return true;
    }

    public static bool IsInValid(this float self)
    {
        return Single.IsInfinity(self);
    }
}