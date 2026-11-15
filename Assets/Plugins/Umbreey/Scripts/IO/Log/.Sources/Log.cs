using System;
using System.Diagnostics;
using Cysharp.Text;

public static class Log
{
    public enum LogLevel : byte
    {
        Verbose = 0, 
        Debug,   
        Info,   
        Warning, 
        Error,  
        None,
    }
    
    public static LogLevel EnableLogLevel = LogLevel.Verbose;
    public static event Action<string> Verbose = PrintLog;
    public static event Action<string> Debug = PrintLog;
    public static event Action<string> Info = PrintLog;
    public static event Action<string> Warning= PrintWarning;
    public static event Action<string> Error = PrintError;
    
    [Conditional("UNITY_EDITOR")]
    public static void V(object o)
    {
        V(o.ToString());
    }
    
    [Conditional("UNITY_EDITOR")]
    public static void V(string str)
    {
        if (EnableLogLevel <= LogLevel.Verbose)
        {
            Verbose?.Invoke(str);
        }
    }
    
    public static void D(object o)
    {
        D(o.ToString());
    }
    
    public static void D(string str)
    {
        if (EnableLogLevel <= LogLevel.Debug)
        {
            Debug?.Invoke(str);
        }
    }
    
    public static void I(object o)
    {
        I(o.ToString());
    }
    
    public static void I(string str)
    {
        if (EnableLogLevel <= LogLevel.Info)
        {
            Info?.Invoke(str);
        }
    }

    public static void W(object o)
    {
        W(o.ToString());
    }
    
    public static void W(string str)
    {
        if (EnableLogLevel <= LogLevel.Warning)
        {
            Warning?.Invoke(str);
        } 
    }
    
    public static void E(object o)
    {
        E(o.ToString());
    }
    
    public static void E(string str)
    {
        if (EnableLogLevel <= LogLevel.Error)
        {
            Error?.Invoke(str);
        }
    }

    private static Utf16ValueStringBuilder Content(string str)
    {
        var sb = ZString.CreateStringBuilder();
        DateTime time = DateTime.Now;
        sb.AppendFormat("{0:00}/{1:00} {2:00}:{3:00}:{4:00}.{5:000}: {6}",
            time.Month,time.Day,time.Hour, time.Minute,time.Second,time.Millisecond,str);
        return sb;
    }
    
    private static void PrintLog(string str)
    {
        using (Utf16ValueStringBuilder sb = Content(str))
        {
            UnityEngine.Debug.Log(sb);
        }
    }
    
    private static void PrintWarning(string str)
    {
        using (Utf16ValueStringBuilder sb = Content(str))
        {
            UnityEngine.Debug.LogWarning(sb);
        }
    }

    private static void PrintError(string str)
    {
        using (Utf16ValueStringBuilder sb = Content(str))
        {
            UnityEngine.Debug.LogError(sb);
        }
    }
}
