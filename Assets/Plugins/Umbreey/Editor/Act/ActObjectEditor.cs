using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Umbreey;
using Object = UnityEngine.Object;

[CustomEditor(typeof(ActObject))]
public class ActObjectEditor :Editor
{
    private ActObject m_ActObject;
    private Dictionary<string, Type> m_ActTypes = new Dictionary<string, Type>();
    private string[] m_Options;
    private static int m_Selectindex=0;
    
    private static List<uint> s_ActIds = new List<uint>();
    
    //预览
    public static GameObject ActSelect;
    public static GameObject ActSelectVice;
    private static bool PausedAfterPlay = false;

    //帧数转时间工具
    private static bool s_FrameToolFoldout = false;
    private static float s_FrameRate = 30f;
    private static float s_FrameCount = 0f;
    private static float s_FrameToolResult = 0f;
    
    private void OnEnable()
    {
        m_ActObject = target as ActObject;
        m_ActTypes.Clear();
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (Type t in a.GetTypes())
            {
                if(t.IsSubclassOf(typeof(ActBase)))
                {
                    System.Attribute[] attrs = System.Attribute.GetCustomAttributes(t);
                    foreach (ActNameAttribute author in attrs)
                    {
                        m_ActTypes[author.label] = t;
                    }
                }
            }
        }

        m_Options = m_ActTypes.Keys.ToArray();
    }
    public override void OnInspectorGUI()
    {
        base.OnInspectorGUI();

        using (new EditorGUILayout.HorizontalScope())
        {
            m_Selectindex = EditorGUILayout.Popup("类型:",m_Selectindex,m_Options);
            if (GUILayout.Button("添加"))
            {
                var key = m_Options[m_Selectindex];
                var act = (ActBase)Activator.CreateInstance(m_ActTypes[key]);
                act.Name = key;
                m_ActObject.ActList.Add(act);
            }
        }
        
        if (GUI.changed)
        {
            RefreshDuration();
        }
        
        bool played = false;
        using (new EditorGUILayout.HorizontalScope())
        {
            ActSelect = (GameObject)EditorGUILayout.ObjectField("预览",(Object)ActSelect, typeof(GameObject));
            if (ActSelect)
            {
                if (GUILayout.Button("播放"))
                {
                    PlayActs();
                    played = true;
                }
            }
        }

        if (ActSelect)
        {
            PausedAfterPlay = GUILayout.Toggle(PausedAfterPlay, "暂停");
            if (PausedAfterPlay && played)
            {
                Debug.Break();
            }
        }

        DrawFrameToTimeTool();
    }

    /// <summary>
    /// 帧数转时间工具：美术按帧配置时，填入帧数并按指定帧率换算成秒。
    /// </summary>
    private void DrawFrameToTimeTool()
    {
        s_FrameToolFoldout = EditorGUILayout.Foldout(s_FrameToolFoldout, "帧数转时间", true);
        if (!s_FrameToolFoldout)
            return;

        using (new EditorGUILayout.HorizontalScope())
        {
            s_FrameRate = Mathf.Max(1f, EditorGUILayout.FloatField("帧率(FPS)", s_FrameRate));
        }
        
        using (new EditorGUILayout.HorizontalScope())
        {
            s_FrameCount = Mathf.Max(0f, EditorGUILayout.FloatField("帧数", s_FrameCount));
        }

        if (s_FrameRate > 0f)
        {
            s_FrameToolResult = s_FrameCount / s_FrameRate;
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("时间(秒)", s_FrameToolResult.ToString("F4"));
            if (GUILayout.Button("复制", GUILayout.Width(50)))
            {
                EditorGUIUtility.systemCopyBuffer = s_FrameToolResult.ToString("F4");
            }
        }
    }

    void PlayActs()
    {
        foreach (var actId in s_ActIds)
        {
            ActDirector.Stop(actId);
        }
        s_ActIds.Clear();

        s_ActIds.Add(ActDirector.Play(m_ActObject,ActSelect,null));
        if (ActSelectVice)
        {
            if (m_ActObject.name.StartsWith("Weapon_") && m_ActObject.name.EndsWith("_char"))
            {
                string othername = m_ActObject.name.Replace("_char", "");
                s_ActIds.Add(ActDirector.Play(othername, ActSelectVice));
            }            
        }

    }

    void RefreshDuration()
    {
        float allDuration = 0;
        foreach (var act in m_ActObject.ActList)
        {
            allDuration = Mathf.Max(allDuration, act.StartTime + act.Duration);
        }
        m_ActObject.Duration = allDuration;
    }
    
}
