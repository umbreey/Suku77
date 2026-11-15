using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static class Vector3Extension
{
    public const float KFloatEpsilon = 0.001f;

    public static bool Approximately(this Vector3 self, Vector3 target, float tolerance = KFloatEpsilon)
    {
        if (Mathf.Abs(self.x - target.x) > tolerance)
            return false;
        if (Mathf.Abs(self.y - target.y) > tolerance)
            return false;
        if (Mathf.Abs(self.z - target.z) > tolerance)
            return false;
        return true;
    }

    public static bool Approximately(this Quaternion self, Quaternion target, float tolerance = KFloatEpsilon)
    {
        if (Mathf.Abs(self.x - target.x) > tolerance)
            return false;
        if (Mathf.Abs(self.y - target.y) > tolerance)
            return false;
        if (Mathf.Abs(self.z - target.z) > tolerance)
            return false;
        if (Mathf.Abs(self.w - target.w) > tolerance)
            return false;
        return true;
    }
    
    public static bool IsValid(this Vector3 self)
    {
        return !IsInValid(self);
    }
    
    public static bool IsInValid(this Vector3 self)
    {
        return float.IsNaN(self.x) || float.IsNaN(self.y) || float.IsNaN(self.z) ||
               float.IsInfinity(self.x) || float.IsInfinity(self.y) || float.IsInfinity(self.z);
    }
}    


