using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Umbreey.Editor
{
    [CustomEditor(typeof(Animation))]
    public class AnimationEditor : UnityEditor.Editor
    {
        private int _selectIndex = 0;
        private Animation _animation;
        private string[] _options;
        private Vector2 _scrollPosition;
        private string _searchFilter = "";
        private void OnEnable()
        {
            _animation = target as Animation;
            List<string> names = new List<string>();
            if (_animation)
            {
                foreach (AnimationState item in _animation)
                {
                    names.Add(item.name);
                }
            }
            _options = names.ToArray();
        }

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            if (Application.isPlaying)
            {
                GUILayout.Space(20f);
                EditorGUILayout.LabelField("动画状态", EditorStyles.boldLabel);
                if (GetPlaying(out var clipName, out var time, out var len,out var looping))
                {
                    GUILayout.Label("");
                    EditorGUI.ProgressBar(GUILayoutUtility.GetLastRect(), time / len, clipName);
                    GUILayout.Label($"Time:{time:F2}/{len:F2} Looping:{looping}");
                }
                else
                {
                    GUILayout.Label("无动画播放中");
                }
                GUILayout.Space(10f);
                
                EditorGUILayout.LabelField("预览播放:", EditorStyles.boldLabel);
                _searchFilter = EditorGUILayout.TextField("搜索:", _searchFilter);
                _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition, GUILayout.MaxHeight(200));
                for (int i = 0; i < _options.Length; i++)
                {
                    if (!string.IsNullOrEmpty(_searchFilter) && !_options[i].ToLower().Contains(_searchFilter.ToLower()))
                        continue;
                    var style = i == _selectIndex ? EditorStyles.boldLabel : EditorStyles.label;
                    if (GUILayout.Button(_options[i], style))
                    {
                        _selectIndex = i;
                        _animation.Play(_options[i]);
                    }
                }
                EditorGUILayout.EndScrollView();
                Repaint();
            }
        }

        bool GetPlaying(out string clipName, out float time, out float len, out bool looping)
        {
            if (_animation.isPlaying)
            {
                foreach (string option in _options)
                {
                    if (this._animation.IsPlaying(option))
                    {
                        var item = _animation[option];
                        clipName = item.name;
                        time = item.time;
                        len=item.length;
                        looping = item.clip.isLooping;
                        return true;
                    }
                }
            }

            clipName = null;
            time = len = 0f;
            looping = false;
            return false;
        }
    }
}