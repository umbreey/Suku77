using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Umbreey.Editor
{
    [CustomEditor(typeof(global::Umbreey.Umbreey))]
    public class UmbreeyEditor : UnityEditor.Editor
    {
        string m_UIStack;
        private int m_DBCount;
        private int m_ModCount;
        private Dictionary<string, AssetBundle> s_AssetBundleDB;
        private StringBuilder m_Content = new StringBuilder();
        private StringBuilder m_DBContent = new StringBuilder();
        private StringBuilder m_ModContent = new StringBuilder();
        private StringBuilder m_PerfContent = new StringBuilder();
        private string m_PerfText;
        private bool m_PerfAB;
        private List<ModBase> m_Mods;
        
        private void OnEnable()
        {
            /*var f = typeof(Assets).GetField("s_CacheAssetObject", BindingFlags.Static | BindingFlags.NonPublic);
            s_AssetBundleDB = f.GetValue(null) as Dictionary<string, AssetBundle>;*/
            EditorApplication.update += Update;
            
            if (Application.isPlaying)
            {
                var f2 = typeof(ModManager).GetField("s_ListMods", BindingFlags.Static | BindingFlags.NonPublic);
                m_Mods = f2.GetValue(null) as List<ModBase>;
            }
        }
        
        private void OnDestroy()
        {
            m_Mods = null;
        }
        
        private void OnDisable()
        {
            m_UIStack = string.Empty;
            EditorApplication.update -= Update;
        }

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            
            GUILayout.Space(20);
            if (!string.IsNullOrEmpty(m_UIStack))
            {
                GUILayout.Label("界面堆栈:");
                GUILayout.Label(m_UIStack,new GUIStyle (){richText = true});
            }

            // UI 开关耗时埋点（canvas-toggle 的 GetComponentsInChildren 成本量化）。
            // 未开启统计时 DumpVisiblePerf 什么都不追加，下面整段自然不会渲染。
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("UI耗时:开")) UIManager.SetVisiblePerf(true);
            if (GUILayout.Button("开+节点数")) UIManager.SetVisiblePerf(true, true);
            if (GUILayout.Button("重置")) UIManager.ResetVisiblePerf();
            if (GUILayout.Button("关")) UIManager.SetVisiblePerf(false);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(m_PerfAB ? "A/B对比:开" : "A/B对比:关"))
            {
                m_PerfAB = !m_PerfAB;
                UIManager.SetVisiblePerfAB(m_PerfAB);
            }
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(m_PerfText))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("UI开关耗时:");
                if (GUILayout.Button("复制", GUILayout.Width(48)))
                    EditorGUIUtility.systemCopyBuffer = m_PerfText;
                GUILayout.EndHorizontal();
                GUILayout.Label(m_PerfText, new GUIStyle() { richText = true });
            }
            if (m_ModCount > 0)
            {
                GUILayout.Label("模块类:");
                GUILayout.Label(m_ModContent.ToString()); 
            }
            
            if (m_DBCount > 0)
            {
                GUILayout.Label("内存ab:");
                GUILayout.Label(m_DBContent.ToString());

                if (GUILayout.Button("GC"))
                {
                    Assets.GC(() =>
                    {
                        Log.I("Done!");
                    });
                }
            }
        }

        void Update()
        {
            bool dirty = false;
            UIManager.GetTaskDebug(m_Content);
            UILock.GetLockDebug(m_Content);
            string txt = m_Content.ToString();
            if (txt != m_UIStack)
            {
                m_UIStack = txt;
                dirty = true;
            }

            m_PerfContent.Clear();
            UIManager.DumpVisiblePerf(m_PerfContent);
            if (m_PerfContent.Length > 0 || !string.IsNullOrEmpty(m_PerfText))
            {
                string perfTxt = m_PerfContent.ToString();
                if (perfTxt != m_PerfText)
                {
                    m_PerfText = perfTxt;
                    dirty = true;
                }
            }
            
            if (s_AssetBundleDB != null && s_AssetBundleDB.Count != m_DBCount)
            {
                m_DBContent.Clear();
                m_DBCount = s_AssetBundleDB.Count;
                var list = s_AssetBundleDB.ToList();
                list.Sort((x, y) => string.Compare(x.Key, y.Key));
                foreach (var (_, bundle) in list)
                {
                    if (bundle != null)
                        m_DBContent.AppendLine(bundle.name);
                }
                dirty = true;
            }

            if (m_Mods != null && m_Mods.Count != m_ModCount)
            {
                m_ModContent.Clear();
                m_ModCount = m_Mods.Count;
                var list = m_Mods.ToList();
                list.Sort((x, y) => string.Compare(x.GetType().Name, y.GetType().Name));
                foreach (var item in list)
                {
                    m_ModContent.AppendLine($"{item.GetType().Name} : {item.GetHashCode()}");
                }
                dirty = true; 
            }
            
            if (dirty)
            {
                Repaint();
            }
        }

    }
}