using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using System;

namespace Umbreey.Editor
{
    [CustomEditor(typeof(UmbreeyAnimation))]
    public class WKAnimationEditor : UnityEditor.Editor
    {
        static class Styles
        {
            public static GUIContent animation = new GUIContent("Animation",
                "The clip that will be played if Play() is called, or if \"Play Automatically\" is enabled");

            public static GUIContent animations = new GUIContent("Animations",
                "These clips will define the States the component will start with");

            public static GUIContent masks = new GUIContent("Masks",
                "These avatar mask will define the States the component will start with");

            public static GUIContent playAutomatically = new GUIContent("Play Automatically",
                "If checked, the default clip will automatically be played");

            public static GUIContent animatePhysics = new GUIContent("Animate Physics",
                "If checked, animations will be updated at the same frequency as Fixed Update");

            public static GUIContent cullingMode = new GUIContent("Culling Mode",
                "Controls what is updated when the object has been culled");
        }

        SerializedProperty clip;
        SerializedProperty states;
        SerializedProperty masks;
        SerializedProperty playAutomatically;
        SerializedProperty animatePhysics;
        SerializedProperty cullingMode;
        private UmbreeyAnimation _animator;
        private int _selectIndex = 0;
        private string[] _options;
        private Vector2 _scrollPosition;
        private string _searchFilter = string.Empty;
        void OnEnable()
        {
            clip = serializedObject.FindProperty("m_Clip");
            states = serializedObject.FindProperty("m_States");
            masks = serializedObject.FindProperty("m_Masks");
            playAutomatically = serializedObject.FindProperty("m_PlayAutomatically");
            animatePhysics = serializedObject.FindProperty("m_AnimatePhysics");
            cullingMode = serializedObject.FindProperty("m_CullingMode");
            _animator = (target as UmbreeyAnimation);
            var names = new List<string>();
            if (_animator != null && _animator.States != null)
            {
                foreach (var state in _animator.States)
                {
                    if (state != null && state.clip != null && !string.IsNullOrEmpty(state.name))
                    {
                        names.Add(state.name);
                    }
                }
            }
            _options = names.ToArray();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(clip, Styles.animation);
            EditorGUILayout.PropertyField(states, Styles.animations, true);
            EditorGUILayout.PropertyField(masks, Styles.masks, true);
            EditorGUILayout.PropertyField(playAutomatically, Styles.playAutomatically);
            EditorGUILayout.PropertyField(animatePhysics, Styles.animatePhysics);
            EditorGUILayout.PropertyField(cullingMode, Styles.cullingMode);

            serializedObject.ApplyModifiedProperties();

            if (Application.isPlaying)
            {
              
                GUILayout.Space(20f);
                EditorGUILayout.LabelField("动画状态", EditorStyles.boldLabel);
                GUILayout.Label("");
                var list = _animator.GetCurrentAnimationStateInfo(0);
                if (list.Count > 0)
                {
                    foreach (var state in list)
                    {
                        var time = state.GetTime();
                        var len = state.Clip.length;
                        EditorGUI.ProgressBar(GUILayoutUtility.GetLastRect(), time / len, state.Name);
                        GUILayout.Label($"Time:{time:F2}/{len:F2} Looping:{state.Clip.isLooping}");
                    }
                }
                else
                {
                    GUILayout.Label("无动画播放中");
                }

                GUILayout.Space(10f);
                
                EditorGUILayout.LabelField("预览播放:", EditorStyles.boldLabel);
                _searchFilter = EditorGUILayout.TextField("搜索:", _searchFilter);
                _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition, GUILayout.MaxHeight(200f));
                for (int i = 0; i < _options.Length; i++)
                {
                    string stateName = _options[i];
                    if (!string.IsNullOrEmpty(_searchFilter) &&
                        stateName.IndexOf(_searchFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    GUIStyle style = i == _selectIndex ? EditorStyles.boldLabel : EditorStyles.label;
                    if (GUILayout.Button(stateName, style))
                    {
                        _animator.Stop();
                        _selectIndex = i;
                        _animator.Play(stateName);
                    }
                }
                EditorGUILayout.EndScrollView();
                GUILayout.Space(10f);
                Repaint();
            }
        }
    }

    [CustomPropertyDrawer(typeof(UmbreeyAnimation.EditorState))]
    class StateDrawer : PropertyDrawer
    {
        class Styles
        {
            public static readonly GUIContent disabledTooltip = new GUIContent("",
                "The Default state cannot be edited, change the Animation clip to change the Default State");
        }

        // Draw the property inside the given rect
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            // Using BeginProperty / EndProperty on the parent property means that
            // prefab override logic works on the entire property.
            EditorGUI.BeginProperty(position, label, property);

            // Draw label
            position = EditorGUI.PrefixLabel(position, GUIUtility.GetControlID(FocusType.Passive), label);

            // Don't make child fields be indented
            var indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;

            // Calculate rects
            Rect clipRect = new Rect(position.x, position.y, position.width / 2 - 5, position.height);
            Rect nameRect = new Rect(position.x + position.width / 2 + 5, position.y, position.width / 2 - 5,
                position.height);


            EditorGUI.BeginDisabledGroup(property.FindPropertyRelative("defaultState").boolValue);
            EditorGUI.PropertyField(nameRect, property.FindPropertyRelative("clip"), GUIContent.none);
            EditorGUI.PropertyField(clipRect, property.FindPropertyRelative("name"), GUIContent.none);
            if (property.FindPropertyRelative("defaultState").boolValue)
            {
                EditorGUI.LabelField(position, Styles.disabledTooltip);
            }

            EditorGUI.EndDisabledGroup();

            // Set indent back to what it was
            EditorGUI.indentLevel = indent;

            EditorGUI.EndProperty();
        }
    }   
}
