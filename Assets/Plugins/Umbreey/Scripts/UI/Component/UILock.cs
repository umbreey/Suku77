using System.Collections.Generic;
using System.Text;
using UnityEngine.EventSystems;
using Umbreey;

namespace Umbreey
{
    public class UILock
    {
        private static EventSystem s_EventSystem;
        private static Dictionary<string, int> s_LockDict = new Dictionary<string, int>();

        private const float TimeOut = 5f;
        static UILock()
        {
            s_EventSystem = EventSystem.current;
        }
        
        public static void GetLockDebug(System.Text.StringBuilder sb)
        {
            //sb.Clear();
            sb.Append("\n<color=white>[UILock] =>");
            foreach (var kvp in s_LockDict)
            {
                sb.Append(kvp.Key);
                sb.Append(":");
                sb.Append(kvp.Value);
                sb.Append(",");
            }
            sb.Append("</color>");
        }

        public static void Lock()
        {
            Lock(string.Empty);
        }

        public static void UnLock()
        {
            UnLock(string.Empty);
        }

        public static void UnLockAll()
        {
            //Log.I($"xys UnLockAll");
            s_LockDict.Clear();
          
            if (s_EventSystem != null)
                s_EventSystem.enabled = true;
        }

        public static void Lock(string reason, float timeOut = TimeOut)
        {
            //Log.I($"[UILock] => Lock:{reason}");
            if (!s_LockDict.TryAdd(reason, 1))
                s_LockDict[reason]++;
            if (s_EventSystem != null)
                s_EventSystem.enabled = false;

            if (timeOut <= 0)
            {
                timeOut = TimeOut;
            }
            TaskManager.Remove(OnTimeOut);
            TaskManager.Timeout(OnTimeOut, timeOut);
        }

        public static void UnLock(string reason)
        {
            //Log.I($"[UILock] => UnLock:{reason}");
            if (s_LockDict.ContainsKey(reason))
            {
                s_LockDict[reason]--;
                if (s_LockDict[reason] <= 0)
                    s_LockDict.Remove(reason);
            }

            if (s_LockDict.Count <= 0)
            {
                if (s_EventSystem != null)
                    s_EventSystem.enabled = true;
            }
        }

        private static void OnTimeOut()
        {   
            //Log.I($"UnLock:OnTimeOut");
            UnLockAll();
        }

        //only debug
        public static void Debug()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"LockKeyCount:{s_LockDict.Count}");
            foreach (var @lock in s_LockDict)
            {
                sb.AppendLine($" reason:{@lock.Key} lockCount:{@lock.Value}");
            }

            Log.I(sb.ToString());
        }
    }
}