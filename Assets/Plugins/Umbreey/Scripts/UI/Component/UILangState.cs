using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Umbreey
{
    public class UILangState : UIState, IUIVisibleNotify
    {
        [Serializable]
        public class LangState
        {
            [ReadOnly] public SystemLanguage language;
            [ReadOnly] public string stateName;
        }
        
        [HideInInspector] public List<LangState> langStates = new List<LangState>();
        
        public void OnEnable()
        {
            SetState(Lang.CurrentLanguage);
        }

        public void AddState(LangState state)
        {
            langStates ??= new List<LangState>();
            langStates.Add(state);
        }

        public void SetState(SystemLanguage language)
        {
            if (HasLanguageState(language))
            {
                var langData = langStates.FirstOrDefault(x => x.language == language);
                if (langData is { stateName: { } })
                {
                    SetState(langData.stateName);
                }
            }
            else if(langStates.Count > 0)
            {
                SetState(langStates[0].stateName);
            }
        }
        
        public bool HasLanguageState(SystemLanguage language)
        {
            return langStates != null && langStates.Exists(x => x.language == language);
        }
    }
    
     #if UNITY_EDITOR
    [CustomEditor(typeof(UILangState))]
    public class UILangStateInspector : UnityEditor.Editor
    {
        private UILangState m_State;
        public SystemLanguage CurrentLanguage { get; set; } = SystemLanguage.English;

        private void OnEnable()
        {
            m_State = target as UILangState;
        }

        private int LanguageIndex { get; set; } = 0;
        private int StateIndex { get; set; } = 0;

        private UIState.State CurrentState => HashState && StateIndex >= 0 && StateIndex < m_State.States.Count ? m_State.States[StateIndex] : null;
        private int StateCount => m_State.langStates?.Count ?? 0;
        bool HashState => StateCount > 0;

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            using (new GUILayout.HorizontalScope())
            {
                var supportArray = Lang.SupportedLanguages.Keys.ToArray();
                LanguageIndex = EditorGUILayout.Popup("新增语言", LanguageIndex, supportArray);
                if (!Lang.SupportedLanguages.TryGetValue(supportArray[LanguageIndex], out var lang))
                {
                    return;
                }
                CurrentLanguage = lang;

                if (GUILayout.Button("新增"))
                {
                    if (m_State.HasLanguageState(CurrentLanguage))
                    {
                        EditorUtility.DisplayDialog("错误", "该语言状态已存在！", "确定");
                        return;
                    }
                    var curState = HashState ? CurrentState : null;
                    StateIndex = StateCount;
                    var newLangState = new UILangState.LangState()
                    {
                        language = CurrentLanguage,
                        stateName = CurrentLanguage.ToString()
                    };
                    var newState = new UIState.State()
                        { stateName = newLangState.stateName };
                    if (curState != null)
                    {
                        newState.stateEnable.AddRange(CopyStateList(curState.stateEnable));
                        newState.statePos.AddRange(CopyStateList(curState.statePos));
                        newState.stateImage.AddRange(CopyStateList(curState.stateImage));
                        newState.stateTextMesh.AddRange(CopyStateList(curState.stateTextMesh));
                        newState.stateText.AddRange(CopyStateList(curState.stateText));
                        newState.stateSelectable.AddRange(CopyStateList(curState.stateSelectable));
                        newState.stateCanvasGroups.AddRange(CopyStateList(curState.stateCanvasGroups));
                    }
                    m_State.AddState(newLangState);
                    m_State.States.Add(newState);
                    TrySave();
                }
            }
            
            using (new GUILayout.HorizontalScope())
            {
                if (HashState)
                {
                    EditorGUILayout.LabelField(CurrentState.stateName);
                    
                    List<string> options = m_State.langStates.ConvertAll<string>((a) => a.stateName);
                    EditorGUI.BeginChangeCheck();
                    StateIndex = EditorGUILayout.Popup(StateIndex, options.ToArray());
                    if (EditorGUI.EndChangeCheck())
                    {
                        TrySave();
                    }
                    options.RemoveAt(StateIndex);
                    
                    if (HashState && GUILayout.Button("删除", GUILayout.Width(220)))
                    {
                        m_State.langStates.RemoveAt(StateIndex);
                        m_State.States.RemoveAt(StateIndex);
                        if (StateIndex >= StateCount)
                            StateIndex = StateCount - 1;
                        else if (m_State.States.Count == 0)
                            StateIndex = -1;
                        TrySave();
                    }
                }
            }

            if (HashState)
                DrawStateInspector();


            if (HashState && GUILayout.Button("增加控制组件", GUILayout.Width(150)))
            {
                var options = new GUIContent[]
                {
                    new GUIContent("隐藏组件"),
                    new GUIContent("坐标组件"),
                    new GUIContent("图片组件"),
                    new GUIContent("文本组件"),
                    //new GUIContent("旧版文本组件")
                    new GUIContent("选择组件"),
                    new GUIContent("CanvasGroup组件")
                   };
                var current = Event.current;
                var mousePosition = current.mousePosition;
                var width = options.Length * 10;
                var height = 100;
                var position = new Rect(mousePosition.x, mousePosition.y - height, width, height);
                var selected = -1;
                EditorUtility.DisplayCustomMenu(position, options, selected,
                    delegate(object callBackUserData, string[] callBackOptions, int callBackSelected) {
                        UIState.State callBackState = callBackUserData as UIState.State;
                        string name = callBackOptions[callBackSelected];

                        switch (name)
                        {
                            case "隐藏组件":
                                    callBackState.stateEnable.Add(new UIState.StateEnable() { enable = true });
                                    break;
                            case "坐标组件":
                                    callBackState.statePos.Add(new UIState.StatePos());
                                    break;
                            case "图片组件":
                                    callBackState.stateImage.Add(new UIState.StateImage());
                                    break;
                            case "文本组件":
                                    callBackState.stateTextMesh.Add(new UIState.StateTextMesh());
                                    break;
                            case "旧版文本组件":
                                    callBackState.stateText.Add(new UIState.StateText());
                                    break;
                            case "选择组件":
                                    callBackState.stateSelectable.Add(new UIState.StateSelectable(){interactable = true});
                                    break;
                            case "CanvasGroup组件":
                                    callBackState.stateCanvasGroups.Add(new UIState.StateCanvasGroup(){interactable = true, blocksRaycasts = true});
                                    break;
                        }
                    }, CurrentState);
            }
        }

        void DrawStateInspector()
        {
            for (int i = 0; i < CurrentState.stateEnable.Count; i++)
            {
                var com = CurrentState.stateEnable[i];
                com.foldout = EditorGUILayout.Foldout(com.foldout, com.name);
                if (com.foldout)
                {
                    EditorGUI.BeginChangeCheck();
                    using (new GUILayout.HorizontalScope())
                    { 
                        com.gameObject = (GameObject)EditorGUILayout.ObjectField("组件", com.gameObject, typeof(GameObject), true);
                        if (GUILayout.Button("删除"))
                        {
                            CurrentState.stateEnable.RemoveAt(i);
                            i--;
                        }
                    }

                    if (com.gameObject)
                    {
                        com.enable = GUILayout.Toggle(com.enable, "激活");
                    }

                    if (EditorGUI.EndChangeCheck())
                    { 
                        TrySave();
                    }
                }
            }

            for (int i = 0; i < CurrentState.statePos.Count; i++)
            {
                var com = CurrentState.statePos[i];
                com.foldout = EditorGUILayout.Foldout(com.foldout, com.name);
                if (com.foldout)
                {
                    EditorGUI.BeginChangeCheck();
                    using (new GUILayout.HorizontalScope())
                    {
                        using (new GUILayout.HorizontalScope()){
                            com.rectTransform =
                                (RectTransform)EditorGUILayout.ObjectField("组件", com.rectTransform, typeof(RectTransform),
                                    true);
                        }
                        if (GUILayout.Button("删除"))
                        {
                            CurrentState.statePos.RemoveAt(i);
                            i--;
                        }
                    }

                    if (com.rectTransform)
                    {
                        EditorGUI.indentLevel = 1;
                        com.anchoredPosition = EditorGUILayout.Vector3Field("Position",
                            (com.anchoredPosition != Vector3.zero)
                                ? com.anchoredPosition
                                : com.rectTransform.anchoredPosition3D);
                        com.sizeDelta = EditorGUILayout.Vector2Field("Width&Height",
                            (com.sizeDelta != Vector2.zero) ? com.sizeDelta : com.rectTransform.sizeDelta);
                        com.pivot = EditorGUILayout.Vector2Field("Pivot", com.rectTransform.pivot);
                        com.roation =
                            EditorGUILayout.Vector3Field("Rotation", com.rectTransform.localRotation.eulerAngles);
                        com.scale = EditorGUILayout.Vector3Field("Scale", com.rectTransform.localScale);
                        com.isSetAnchor = EditorGUILayout.Toggle("设置Anchor", com.isSetAnchor);
                        if (com.isSetAnchor)
                        {
                            com.anchorMin = EditorGUILayout.Vector2Field("Anchor最小值", com.anchorMin);
                            com.anchorMan = EditorGUILayout.Vector2Field("Anchor最大值", com.anchorMan);
                        }
                        com.isSetOffset = EditorGUILayout.Toggle("设置Offset", com.isSetOffset);
                        if (com.isSetOffset)
                        {
                            com.offsetMin = EditorGUILayout.Vector2Field("Offset最小值", com.offsetMin);
                            com.offsetMax = EditorGUILayout.Vector2Field("Offset最大值", com.offsetMax);
                        }

                        EditorGUI.indentLevel = 0;
                    }

                    if (EditorGUI.EndChangeCheck())
                    {
                        TrySave();
                    }
                }
            }

            for (int i = 0; i < CurrentState.stateImage.Count; i++)
            {
                var com = CurrentState.stateImage[i];
                com.foldout = EditorGUILayout.Foldout(com.foldout, com.name);
                if (com.foldout)
                {
                    EditorGUI.BeginChangeCheck();
                    using (new GUILayout.HorizontalScope())
                    {
                        using (new GUILayout.VerticalScope())
                        {
                            com.image = (Image)EditorGUILayout.ObjectField("组件", com.image, typeof(Image), true);
                            if (Application.isPlaying)
                                com.uiImage =
                                    (UIImage)EditorGUILayout.ObjectField("组件", com.uiImage, typeof(UIImage), true);
                        }

                        if (GUILayout.Button("删除"))
                        {
                            CurrentState.stateImage.RemoveAt(i);
                            TrySave();
                            i--;
                        }
                    }

                    if (com.image)
                    {
                        EditorGUI.indentLevel = 1;
                        com.sprite = (Sprite)EditorGUILayout.ObjectField("精灵",
                            (com.sprite != null) ? com.sprite : com.image.sprite, typeof(Sprite), true,
                            GUILayout.Height(EditorGUIUtility.singleLineHeight));
                        com.color = EditorGUILayout.ColorField("颜色", com.color);
                        com.isNativeSize = GUILayout.Toggle(com.isNativeSize, "自适应");
                        com.notSetSprite = GUILayout.Toggle(com.notSetSprite, "不修改精灵图");
                        EditorGUI.indentLevel = 0;
                    }

                    if (com.uiImage)
                    {
                        EditorGUI.indentLevel = 1;
                        com.spriteName = (string)EditorGUILayout.TextField("精灵",com.spriteName,
                            GUILayout.Height(EditorGUIUtility.singleLineHeight));
                        com.color = EditorGUILayout.ColorField("颜色", com.color);
                        com.isNativeSize = GUILayout.Toggle(com.isNativeSize, "自适应");
                        com.notSetSprite = GUILayout.Toggle(com.notSetSprite, "不修改精灵图");
                        EditorGUI.indentLevel = 0;
                    }
                    
                    if (EditorGUI.EndChangeCheck())
                    {
                        TrySave();
                    }
                }
            }

            for (int i = 0; i < CurrentState.stateTextMesh.Count; i++)
            {
                var com = CurrentState.stateTextMesh[i];
                com.foldout = EditorGUILayout.Foldout(com.foldout, com.name);
                if (com.foldout)
                {
                    EditorGUI.BeginChangeCheck();
                    using (new GUILayout.HorizontalScope())
                    {
                        using (new GUILayout.VerticalScope())
                        {
                            com.textMesh =
                                (TextMeshProUGUI)EditorGUILayout.ObjectField("组件", com.textMesh,
                                    typeof(TextMeshProUGUI), true);
                            if (Application.isPlaying)
                            {
                                com.uiTextMesh =
                                    (UITextMeshPro)EditorGUILayout.ObjectField("组件", com.uiTextMesh,
                                        typeof(UIText),
                                        true);
                            }
                        }

                        if (GUILayout.Button("删除"))
                        {
                            CurrentState.stateTextMesh.RemoveAt(i);
                            TrySave();
                            i--;
                        }
                    }

                    if (com.textMesh)
                    {
                        EditorGUI.indentLevel = 1;
                        com.font = (TMP_FontAsset)EditorGUILayout.ObjectField("字体",
                            (com.font != null) ? com.font : com.textMesh.font, typeof(TMP_FontAsset), true);
                        Material originalMaterial = com.textMesh.fontMaterial;
                        if (originalMaterial != null && originalMaterial.name.EndsWith("(Instance)"))
                        {
                            // 获取材质本体
                            originalMaterial = Resources.FindObjectsOfTypeAll<Material>()
                                .FirstOrDefault(mat => mat.name == originalMaterial.name.Replace(" (Instance)", ""));
                        }
                        com.material = (Material)EditorGUILayout.ObjectField("样式",
                            (com.material != null) ? com.material : originalMaterial, typeof(Material), true);
                        com.enableContent = EditorGUILayout.Toggle("启动文本", com.enableContent);
                        if (com.enableContent)
                        {
                            com.content = EditorGUILayout.TextField("文本",
                                (!string.IsNullOrEmpty(com.content) ? com.content : com.textMesh.text));
                        }
                        com.color = EditorGUILayout.ColorField("颜色", com.color);
                        com.fontSize = EditorGUILayout.FloatField("大小",
                            (com.fontSize > 0 ? com.fontSize : com.textMesh.fontSize));
                        EditorGUI.indentLevel = 0;
                    }

                    if (com.uiTextMesh)
                    {
                        EditorGUI.indentLevel = 1;
                        com.fontName = EditorGUILayout.TextField("字体", com.fontName);
                        com.materialName = EditorGUILayout.TextField("样式", com.materialName);
                        com.enableContent = EditorGUILayout.Toggle("启动文本", com.enableContent);
                        if (com.enableContent)
                        {
                            com.content = EditorGUILayout.TextField("文本",
                                (!string.IsNullOrEmpty(com.content) ? com.content : com.textMesh.text));
                        }

                        com.color = EditorGUILayout.ColorField("颜色", com.color);
                        com.fontSize = EditorGUILayout.FloatField("大小",
                            (com.fontSize > 0 ? com.fontSize : com.textMesh.fontSize));
                        EditorGUI.indentLevel = 0;
                    }

                    if (EditorGUI.EndChangeCheck())
                    {
                        TrySave();
                    }
                }
            }
                
            for (int i = 0; i < CurrentState.stateText.Count; i++)
            {
                var com = CurrentState.stateText[i];
                com.foldout = EditorGUILayout.Foldout(com.foldout, com.name);
                if (com.foldout)
                {
                    EditorGUI.BeginChangeCheck();
                    using (new GUILayout.HorizontalScope())
                    {
                        using (new GUILayout.VerticalScope())
                        {
                            com.text =
                                (Text)EditorGUILayout.ObjectField("组件", com.text,
                                    typeof(Text), true);
                            if (Application.isPlaying)
                            {
                                com.uiText =
                                    (UIText)EditorGUILayout.ObjectField("组件", com.uiText,
                                        typeof(UIText),
                                        true);
                            }
                        }

                        if (GUILayout.Button("删除"))
                        {
                            CurrentState.stateText.RemoveAt(i);
                            TrySave();
                            i--;
                        }
                    }

                    if (com.text)
                    {
                        EditorGUI.indentLevel = 1;
                        com.font = (Font)EditorGUILayout.ObjectField("字体",
                            (com.font != null) ? com.font : com.text.font, typeof(Font), true);
                        com.material = (Material)EditorGUILayout.ObjectField("样式",
                            (com.material != null) ? com.material : com.text.material, typeof(Material), true);
                        com.enableContent = EditorGUILayout.Toggle("启动文本", com.enableContent);
                        if (com.enableContent)
                        {
                            com.content = EditorGUILayout.TextField("文本",
                                (!string.IsNullOrEmpty(com.content) ? com.content : com.text.text));
                        }
                        com.color = EditorGUILayout.ColorField("颜色", com.color);
                        com.fontSize = EditorGUILayout.IntField("大小",
                            (com.fontSize > 0 ? com.fontSize : com.text.fontSize));
                        EditorGUI.indentLevel = 0;
                    }

                    if (com.uiText)
                    {
                        EditorGUI.indentLevel = 1;
                        com.fontName = EditorGUILayout.TextField("字体", com.fontName);
                        com.materialName = EditorGUILayout.TextField("样式", com.materialName);
                        com.enableContent = EditorGUILayout.Toggle("启动文本", com.enableContent);
                        if (com.enableContent)
                        {
                            com.content = EditorGUILayout.TextField("文本",
                                (!string.IsNullOrEmpty(com.content) ? com.content : com.text.text));
                        }

                        com.color = EditorGUILayout.ColorField("颜色", com.color);
                        com.fontSize = EditorGUILayout.IntField("大小",
                            (com.fontSize > 0 ? com.fontSize : com.text.fontSize));
                        EditorGUI.indentLevel = 0;
                    }

                    if (EditorGUI.EndChangeCheck())
                    {
                        TrySave();
                    }
                }
            }

            for (int i = 0; i < CurrentState.stateSelectable.Count; i++)
            {
                var com = CurrentState.stateSelectable[i];
                com.foldout = EditorGUILayout.Foldout(com.foldout, com.name);
                if (com.foldout)
                {
                    EditorGUI.BeginChangeCheck();
                    using (new GUILayout.HorizontalScope())
                    {
                        using (new GUILayout.VerticalScope())
                        {
                            com.selectable =
                                (Selectable)EditorGUILayout.ObjectField("组件", com.selectable,
                                    typeof(Selectable), true);
                        }

                        if (GUILayout.Button("删除"))
                        {
                            CurrentState.stateSelectable.RemoveAt(i);
                            TrySave();
                            i--;
                        }
                    }
                        
                    if (com.selectable)
                    {
                        EditorGUI.indentLevel = 1;
                        com.interactable = EditorGUILayout.Toggle("可交互", com.interactable);
                        EditorGUI.indentLevel = 0;
                    }
                }
            }
                
            for (int i = 0; i < CurrentState.stateCanvasGroups.Count; i++)
            {
                var com = CurrentState.stateCanvasGroups[i];
                com.foldout = EditorGUILayout.Foldout(com.foldout, com.name);
                if (com.foldout)
                {
                    EditorGUI.BeginChangeCheck();
                    using (new GUILayout.HorizontalScope())
                    {
                        using (new GUILayout.VerticalScope())
                        {
                            com.canvasGroup =
                                (CanvasGroup)EditorGUILayout.ObjectField("组件", com.canvasGroup,
                                    typeof(CanvasGroup), true);
                        }

                        if (GUILayout.Button("删除"))
                        {
                            CurrentState.stateCanvasGroups.RemoveAt(i);
                            TrySave();
                            i--;
                        }
                    }
                        
                    if (com.canvasGroup)
                    {
                        EditorGUI.indentLevel = 1;
                        com.interactable = EditorGUILayout.Toggle("可交互", com.interactable);
                        com.blocksRaycasts = EditorGUILayout.Toggle("BlocksRaycasts", com.blocksRaycasts);
                        com.alpha = EditorGUILayout.Slider("透明度", com.alpha, 0, 1);
                        EditorGUI.indentLevel = 0;
                    }
                }
            }
        }

        public void SetDefault()
        {
            HashSet<GameObject> hash = new HashSet<GameObject>();
            for (int i = 0; i < m_State.States.Count; i++)
            {
                foreach (var com in m_State.States[i].stateEnable)
                {
                    if (com.gameObject)
                        hash.Add(com.gameObject);
                }

                foreach (var com in m_State.States[i].statePos)
                {
                    if (com.rectTransform)
                        hash.Add(com.rectTransform.gameObject);
                }

                foreach (var com in m_State.States[i].stateImage)
                {
                    if (com.image)
                        hash.Add(com.image.gameObject);
                }

                foreach (var com in m_State.States[i].stateTextMesh)
                {
                    if (com.textMesh)
                        hash.Add(com.textMesh.gameObject);
                }
                    
                foreach (var com in m_State.States[i].stateSelectable)
                {
                    if (com.selectable)
                        hash.Add(com.selectable.gameObject);
                }
                    
                foreach (var com in m_State.States[i].stateCanvasGroups)
                {
                    if (com.canvasGroup)
                        hash.Add(com.canvasGroup.gameObject);
                }
            }

            m_State.Defaults.Clear();
            m_State.Defaults.AddRange(hash);
        }

        void TrySave()
        {
            SetDefault();
            if (CurrentState != null)
            {
                string stateName = CurrentState.stateName;
                m_State.SetState(stateName);
            }
            
            EditorUtility.SetDirty(m_State);
        }
            
        List<T> CopyStateList<T>(List<T> list) where T : UIState.StateBase
        {
            List<T> newList = new List<T>();
            foreach (var item in list)
            {
                var type = item.GetType();
                var newItem = (T)System.Activator.CreateInstance(type);
                newItem.Copy(item);
                newList.Add(newItem);
            }
            return newList;
        }
    }
#endif
}
