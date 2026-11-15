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
    public class UIState : MonoBehaviour, IExport,IExportProcess
    {
        [HideInInspector] public string CurrentState;
        [HideInInspector] public List<State> States = new List<State>();
        [HideInInspector] public List<GameObject> Defaults = new List<GameObject>();

        private HashSet<GameObject> m_Selected = new HashSet<GameObject>();

        void Awake()
        {
            CurrentState = "";
        }

        public void ResetState(string state)
        {
            CurrentState = "";
            SetState(state);
        }

        public void SetState(string state)
        {
            if (CurrentState != state)
            {
                bool hasState = false;
                for (int i = 0; i < States.Count; i++)
                {
                    if (States[i].stateName == state)
                    {
                        hasState = true;
                        SetState(i);
                    }
                }
#if UNITY_EDITOR
                if(!hasState && !string.IsNullOrEmpty(state))
                    Log.E("UIState:没有找到状态 " + state);
#endif
            }
        }
        
        private void SetState(int index)
        {
            if (index >= 0 && index < States.Count)
            {
                m_Selected.Clear();
                CurrentState = States[index].stateName;
                foreach (var state in States[index].stateEnable)
                {
                    state.Set();
                    m_Selected.Add(state.gameObject);
                }

                foreach (var state in States[index].statePos)
                {
                    state.Set();
                    m_Selected.Add(state.rectTransform.gameObject);
                }

                foreach (var state in States[index].stateImage)
                {
                    state.Set();
                    m_Selected.Add(state.go);
                }

                foreach (var state in States[index].stateTextMesh)
                {
                    state.Set();
                    m_Selected.Add(state.go);
                }
                
                foreach (var state in States[index].stateText)
                {
                    state.Set();
                    m_Selected.Add(state.go);
                }

                foreach (var state in States[index].stateSelectable)
                {
                    state.Set();
                    m_Selected.Add(state.go);
                }
                
                foreach (var state in States[index].stateCanvasGroups)
                {
                    state.Set();
                    m_Selected.Add(state.go);
                }
                
                foreach (var state in States[index].stateGray)
                {
                    state.Set();
                    m_Selected.Add(state.go);
                }
                
                foreach (var state in States[index].stateUIStates)
                {
                    state.Set();
                    m_Selected.Add(state.go);
                }

                foreach (var defau in Defaults)
                {
                    if (defau && !m_Selected.Contains(defau))
                        defau.gameObject.SetActive(false);
                }
            }
        }

        [System.Serializable]
        public class State
        {
            public string stateName;
            public List<StateEnable> stateEnable = new List<StateEnable>();
            public List<StatePos> statePos = new List<StatePos>();
            public List<StateImage> stateImage = new List<StateImage>();
            public List<StateTextMesh> stateTextMesh = new List<StateTextMesh>();
            public List<StateText> stateText = new List<StateText>();
            public List<StateSelectable> stateSelectable = new List<StateSelectable>();
            public List<StateCanvasGroup> stateCanvasGroups = new List<StateCanvasGroup>();
            public List<StateGray> stateGray = new List<StateGray>();
            public List<StateUIState> stateUIStates = new List<StateUIState>();
        }

        [System.Serializable]
        public class StateBase
        {
            public virtual void Copy(StateBase other)
            {
                
            }
        }

        [System.Serializable]
        public class StateEnable : StateBase
        {
            [System.NonSerialized] public bool foldout = true;
            [System.NonSerialized] public string name = "隐藏组件";

            public GameObject gameObject;
            public bool enable = true;

            public void Set()
            {
                if (gameObject)
                    gameObject.SetActive(enable);
            }
            
            public override void Copy(StateBase other)
            {
                if (other is StateEnable stateEnable)
                {
                    this.gameObject = stateEnable.gameObject;
                    this.enable = stateEnable.enable;
                }
            }
        }

        [System.Serializable]
        public class StatePos : StateBase
        {
            [System.NonSerialized] public bool foldout = true;
            [System.NonSerialized] public string name = "变换组件";
            public RectTransform rectTransform;
            public Vector3 anchoredPosition = Vector3.zero;
            public Vector2 sizeDelta = Vector3.zero;
            public Vector3 roation = Quaternion.identity.eulerAngles;
            public Vector3 scale = Vector3.one;
            public Vector2 pivot = Vector2.one * 0.5f;
            public bool isSetAnchor = false;
            public Vector2 anchorMin=Vector2.one * 0.5f;
            public Vector2 anchorMan=Vector2.one * 0.5f;
            public bool isSetOffset = false;
            public Vector2 offsetMin=Vector2.zero;
            public Vector2 offsetMax=Vector2.zero;

            public void Set()
            {
                if (rectTransform)
                {
                    rectTransform.gameObject.SetActive(true);
                    rectTransform.anchoredPosition3D = anchoredPosition;
                    rectTransform.sizeDelta = sizeDelta;
                    rectTransform.pivot = pivot;
                    rectTransform.localRotation = Quaternion.Euler(roation);
                    rectTransform.localScale = scale;
                    
                    if (isSetAnchor)
                    {
                        rectTransform.anchorMin = anchorMin;
                        rectTransform.anchorMax = anchorMan;
                    }

                    if (isSetOffset)
                    {
                        rectTransform.offsetMax = offsetMin;
                        rectTransform.offsetMin = offsetMax;
                    }
                }
            }

            public override void Copy(StateBase other)
            {
                if(other is StatePos statePos)
                {
                    this.rectTransform = statePos.rectTransform;
                    this.anchoredPosition = statePos.anchoredPosition;
                    this.sizeDelta = statePos.sizeDelta;
                    this.roation = statePos.roation;
                    this.scale = statePos.scale;
                    this.pivot = statePos.pivot;
                    this.isSetAnchor = statePos.isSetAnchor;
                    this.anchorMin = statePos.anchorMin;
                    this.anchorMan = statePos.anchorMan;
                    this.isSetOffset = statePos.isSetOffset;
                    this.offsetMin = statePos.offsetMin;
                    this.offsetMax = statePos.offsetMax;
                }
            }
        }

        [System.Serializable]
        public class StateImage : StateBase
        {
            [System.NonSerialized] public bool foldout = true;
            [System.NonSerialized] public string name = "图片组件";
            public GameObject go;
            public Image image;
            public UIImage uiImage;
            public Sprite sprite;
            public string spriteName;
            public Color color = Color.white;
            public bool isNativeSize = false;
            public bool notSetSprite = false;

            public void Set()
            {
                if (!image && !uiImage)
                    uiImage = go.GetComponent<UIImage>();

                if (uiImage)
                {
                    go = uiImage.gameObject;
                    go.SetActive(true);
                    if(!notSetSprite)
                        uiImage.SpriteName = spriteName;
                    uiImage.color = color;
                    uiImage.isNativeSize = isNativeSize;
                }else if (image)
                {
                    go = image.gameObject;
                    go.SetActive(true);
                    if(!notSetSprite)
                        image.sprite = sprite;
                    image.color = color;
                    if (isNativeSize)
                        image.SetNativeSize();
                }
            }
            
            public override void Copy(StateBase other)
            {
                if(other is StateImage stateImage)
                {
                    this.go = stateImage.go;
                    this.image = stateImage.image;
                    this.uiImage = stateImage.uiImage;
                    this.sprite = stateImage.sprite;
                    this.spriteName = stateImage.spriteName;
                    this.color = stateImage.color;
                    this.isNativeSize = stateImage.isNativeSize;
                    this.notSetSprite = stateImage.notSetSprite;
                }
            }
        }

        [System.Serializable]
        public class StateTextMesh : StateBase
        {
            [System.NonSerialized] public bool foldout = true;
            [System.NonSerialized] public string name = "字体组件";
            public GameObject go;
            public TextMeshProUGUI textMesh;
            public UITextMeshPro uiTextMesh;
            public TMP_FontAsset font;
            public string fontName;
            public Color color = Color.white;
            public Material material;
            public string materialName;
            public bool enableContent = true;
            public string content;
            public float fontSize = 0;
            public float lineSpacing = 0;
            public float characterSpacing = 0;
            public TextAlignmentOptions alignment = TextAlignmentOptions.Center;
            
            public void Set()
            {
                if (!textMesh && !uiTextMesh && go)
                    uiTextMesh = go.GetComponent<UITextMeshPro>();

                if (uiTextMesh)
                {
                    go = uiTextMesh.gameObject;
                    go.SetActive(true);
                    uiTextMesh.FontName = fontName;
                    if(enableContent)
                        uiTextMesh.text = content;
                    uiTextMesh.color = color;
                    uiTextMesh.fontSize = (int)fontSize;
                    uiTextMesh.lineSpacing = lineSpacing;
                    uiTextMesh.characterSpacing = characterSpacing;
                    if(!string.IsNullOrEmpty(materialName))
                        uiTextMesh.MaterialName = materialName;
                }else if (textMesh)
                {
                    go = textMesh.gameObject;
                    go.SetActive(true);
                    textMesh.font = font;
                    if(enableContent)
                        textMesh.text = content;
                    textMesh.color = color;
                    textMesh.fontSize = fontSize;
                    textMesh.fontMaterial = material;
                    textMesh.lineSpacing = lineSpacing;
                    textMesh.characterSpacing = characterSpacing;
                    textMesh.alignment = alignment;
                }
            }

            public override void Copy(StateBase other)
            {
                if(other is StateTextMesh stateTextMesh)
                {
                    this.go = stateTextMesh.go;
                    this.textMesh = stateTextMesh.textMesh;
                    this.uiTextMesh = stateTextMesh.uiTextMesh;
                    this.font = stateTextMesh.font;
                    this.fontName = stateTextMesh.fontName;
                    this.color = stateTextMesh.color;
                    this.material = stateTextMesh.material;
                    this.materialName = stateTextMesh.materialName;
                    this.enableContent = stateTextMesh.enableContent;
                    this.content = stateTextMesh.content;
                    this.fontSize = stateTextMesh.fontSize;
                    this.lineSpacing = stateTextMesh.lineSpacing;
                    this.characterSpacing = stateTextMesh.characterSpacing;
                    this.alignment = stateTextMesh.alignment;
                }
            }
        }

        [System.Serializable]
        public class StateText : StateBase
        {
            [System.NonSerialized] public bool foldout = true;
            [System.NonSerialized] public string name = "旧版字体组件";
            public GameObject go;
            public Text text;
            public UIText uiText;
            public Font font;
            public string fontName;
            public Color color = Color.white;
            public Material material;
            public string materialName;
            public bool enableContent = true;
            public string content;
            public int fontSize = 0;
            public float lineSpacing = 0;

            public void Set()
            {
                if (!text && !uiText && go)
                    uiText = go.GetComponent<UIText>();

                if (uiText)
                {
                    go = uiText.gameObject;
                    go.SetActive(true);
                    uiText.FontName = fontName;
                    if(enableContent)
                        uiText.text = content;
                    uiText.color = color;
                    uiText.fontSize = (int)fontSize;
                    uiText.lineSpacing = lineSpacing;
                    if(!string.IsNullOrEmpty(materialName))
                        uiText.MaterialName = materialName;
                }else if (text)
                {
                    go = text.gameObject;
                    go.SetActive(true);
                    text.font = font;
                    if(enableContent)
                        text.text = content;
                    text.color = color;
                    text.fontSize = fontSize;
                    text.material = material;
                    text.lineSpacing  = lineSpacing;
                }
            }

            public override void Copy(StateBase other)
            {
                if(other is StateText stateText)
                {
                    this.go = stateText.go;
                    this.text = stateText.text;
                    this.uiText = stateText.uiText;
                    this.font = stateText.font;
                    this.fontName = stateText.fontName;
                    this.color = stateText.color;
                    this.material = stateText.material;
                    this.materialName = stateText.materialName;
                    this.enableContent = stateText.enableContent;
                    this.content = stateText.content;
                    this.fontSize = stateText.fontSize;
                    this.lineSpacing = stateText.lineSpacing;
                }
            }
        }

        [System.Serializable]
        public class StateSelectable : StateBase
        {
            [System.NonSerialized] public bool foldout = true;
            [System.NonSerialized] public string name = "选择组件";
            public GameObject go;
            public Selectable selectable;
            public bool interactable = true;

            public void Set()
            {
                if (!selectable && go)
                    selectable = go.GetComponent<Selectable>();
                if (selectable)
                {
                    go = selectable.gameObject;
                    go.SetActive(true);
                    selectable.interactable = interactable;
                }
            }
            
            public override void Copy(StateBase other)
            {
                if(other is StateSelectable stateSelectable)
                {
                    this.go = stateSelectable.go;
                    this.selectable = stateSelectable.selectable;
                    this.interactable = stateSelectable.interactable;
                }
            }
        }
        
        [System.Serializable]
        public class StateCanvasGroup : StateBase
        {
            [System.NonSerialized] public bool foldout = true;
            [System.NonSerialized] public string name = "CanvasGroup组件";
            public GameObject go;
            public CanvasGroup canvasGroup;
            public bool interactable = true;
            public bool blocksRaycasts = true;
            public float alpha = 1;

            public void Set()
            {
                if (!canvasGroup && go)
                    canvasGroup = go.GetComponent<CanvasGroup>();
                if (canvasGroup)
                {
                    go = canvasGroup.gameObject;
                    go.SetActive(true);
                    canvasGroup.interactable = interactable;
                    canvasGroup.blocksRaycasts = blocksRaycasts;
                    canvasGroup.alpha = alpha;
                }
            }
            
            public override void Copy(StateBase other)
            {
                if(other is StateCanvasGroup stateCanvasGroup)
                {
                    this.go = stateCanvasGroup.go;
                    this.canvasGroup = stateCanvasGroup.canvasGroup;
                    this.interactable = stateCanvasGroup.interactable;
                    this.blocksRaycasts = stateCanvasGroup.blocksRaycasts;
                    this.alpha = stateCanvasGroup.alpha;
                }
            }
        }
        
        [System.Serializable]
        public class StateGray : StateBase
        {
            [System.NonSerialized] public bool foldout = true;
            [System.NonSerialized] public string name = "灰度组件";
            public GameObject go;
            public bool isGray = true;

            public void Set()
            {
                string materialName = isGray ? "M_Gray.mat" : null;
                foreach (var mono in go.GetComponentsInChildren<MonoBehaviour>())
                {
                    if (mono is UIImage uiImage)
                    {
                        uiImage.MaterialName = materialName;
                    } else if (mono is UIRawImage uiRawImage)
                    {
                        uiRawImage.MaterialName = materialName;
                    } else if (mono is Image image)
                    {
                        image.material = isGray ? UGUIHelper.Gray : null;
                    } else if (mono is RawImage rawImage)
                    {
                        rawImage.material = isGray ? UGUIHelper.Gray : null;
                    } 
                }
            }
            
            public override void Copy(StateBase other)
            {
                if(other is StateGray stateGray)
                {
                    this.go = stateGray.go;
                    this.isGray = stateGray.isGray;
                }
            }
        }
        
        [System.Serializable]
        public class StateUIState : StateBase
        {
            [System.NonSerialized] public bool foldout = true;
            [System.NonSerialized] public string name = "UIState组件";
            public GameObject go;
            public UIState uiState;
            public string state = "";

            public void Set()
            {
                if (go)
                {
                    go.SetActive(true);
                    uiState = go.GetComponent<UIState>();
                    if(uiState)
                        uiState.SetState(state);
                }
            }
            
            public override void Copy(StateBase other)
            {
                if(other is StateUIState stateUIState)
                {
                    this.go = stateUIState.go;
                    this.state = stateUIState.state;
                }
            }
        }

        
 #if UNITY_EDITOR
        [CustomEditor(typeof(UIState))]
        public class UIStateInspector : UnityEditor.Editor
        {
            private UIState m_State;
            public bool AllowStateModification = true;

            private void OnEnable()
            {
                m_State = target as UIState;
                stateIndex = m_State.States.FindIndex((state) => { return state.stateName == m_State.CurrentState; });

            }

            private int _index = 0;

            private int stateIndex
            {
                get
                {
                    if (_index < 0 || _index >= m_State.States.Count)
                    {
                        _index = 0;
                    }
                    return _index;
                }
                set { _index = value; }
            }

            private UIState.State currentState => stateIndex < m_State.States.Count ? m_State.States[stateIndex] : null;
            private int stateCount => m_State.States?.Count??0;
            bool hashState => stateCount > 0;

            public override void OnInspectorGUI()
            {
                base.OnInspectorGUI();

                using (new GUILayout.HorizontalScope())
                {
                    if (hashState)
                    {
                        List<string> options = m_State.States.ConvertAll<string>((a) => { return a.stateName; });
                        EditorGUI.BeginChangeCheck();
                        stateIndex = EditorGUILayout.Popup(stateIndex, options.ToArray());
                        if (EditorGUI.EndChangeCheck())
                        {
                            TrySave();
                        }

                        GUI.enabled = AllowStateModification;
                        string textField = EditorGUILayout.TextField(currentState.stateName);
                        GUI.enabled = true;
                        
                        options.RemoveAt(stateIndex);
                        currentState.stateName = ObjectNames.GetUniqueName(options.ToArray(), textField);
                    }

                    if (AllowStateModification)
                    {
                        if (GUILayout.Button("新增"))
                        {
                            var curState = hashState ? currentState : null;
                            List<string> options = m_State.States.ConvertAll<string>((a) => { return a.stateName; });
                            stateIndex = stateCount;
                            var newState = new UIState.State()
                            { stateName = ObjectNames.GetUniqueName(options.ToArray(), "state") };
                            if (curState != null)
                            {
                                newState.stateEnable.AddRange(CopyStateList(curState.stateEnable));
                                newState.statePos.AddRange(CopyStateList(curState.statePos));
                                newState.stateImage.AddRange(CopyStateList(curState.stateImage));
                                newState.stateTextMesh.AddRange(CopyStateList(curState.stateTextMesh));
                                newState.stateText.AddRange(CopyStateList(curState.stateText));
                                newState.stateSelectable.AddRange(CopyStateList(curState.stateSelectable));
                                newState.stateCanvasGroups.AddRange(CopyStateList(curState.stateCanvasGroups));
                                newState.stateGray.AddRange(CopyStateList(curState.stateGray));
                                newState.stateUIStates.AddRange(CopyStateList(curState.stateUIStates));
                            }
                            m_State.States.Add(newState);
                            TrySave();
                        }

                        if (hashState && GUILayout.Button("删除"))
                        {
                            m_State.States.RemoveAt(stateIndex);
                            if (stateIndex >= stateCount)
                                stateIndex = stateCount - 1;
                            else if (m_State.States.Count == 0)
                                stateIndex = -1;
                            TrySave();
                        }
                    }
                }

                if (hashState)
                    DrawStateInspector();


                if (hashState && GUILayout.Button("增加控制组件", GUILayout.Width(150)))
                {
                    var options = new GUIContent[]
                    {
                        new GUIContent("隐藏组件"),
                        new GUIContent("坐标组件"),
                        new GUIContent("图片组件"),
                        new GUIContent("文本组件"),
                        new GUIContent("旧版文本组件"),
                        new GUIContent("选择组件"),
                        new GUIContent("CanvasGroup组件"),
                        new GUIContent("灰度组件"),
                        new GUIContent("UIState组件"),
                    };
                    var current = Event.current;
                    var mousePosition = current.mousePosition;
                    var width = options.Length * 10;
                    var height = 100;
                    var position = new Rect(mousePosition.x, mousePosition.y - height, width, height);
                    var selected = -1;
                    EditorUtility.DisplayCustomMenu(position, options, selected,
                        delegate(object callBackUserData, string[] callBackOptions, int callBackSelected)
                        {
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
                                case "灰度组件":
                                    callBackState.stateGray.Add(new UIState.StateGray(){isGray = true});
                                    break;
                                case "UIState组件":
                                    callBackState.stateUIStates.Add(new UIState.StateUIState(){state = ""});
                                    break;
                            }
                        }, currentState);
                }
            }

            void DrawStateInspector()
            {
                for (int i = 0; i < currentState.stateEnable.Count; i++)
                {
                    var com = currentState.stateEnable[i];
                    com.foldout = EditorGUILayout.Foldout(com.foldout, com.name);
                    if (com.foldout)
                    {
                        EditorGUI.BeginChangeCheck();
                        using (new GUILayout.HorizontalScope())
                        {
                            com.gameObject =
                                (GameObject)EditorGUILayout.ObjectField("组件", com.gameObject, typeof(GameObject), true);
                            if (GUILayout.Button("删除"))
                            {
                                currentState.stateEnable.RemoveAt(i);
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

                for (int i = 0; i < currentState.statePos.Count; i++)
                {
                    var com = currentState.statePos[i];
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
                                currentState.statePos.RemoveAt(i);
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

                for (int i = 0; i < currentState.stateImage.Count; i++)
                {
                    var com = currentState.stateImage[i];
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
                                currentState.stateImage.RemoveAt(i);
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

                for (int i = 0; i < currentState.stateTextMesh.Count; i++)
                {
                    var com = currentState.stateTextMesh[i];
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
                                currentState.stateTextMesh.RemoveAt(i);
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
                            com.lineSpacing = EditorGUILayout.FloatField("LineSpacing",
                                (com.lineSpacing > 0 ? com.lineSpacing : com.textMesh.lineSpacing));
                            com.characterSpacing = EditorGUILayout.FloatField("CharacterSpacing",
                                (com.characterSpacing > 0 ? com.characterSpacing : com.textMesh.characterSpacing));
                            com.alignment = (TextAlignmentOptions)EditorGUILayout.EnumFlagsField(
                                "Alignment",
                                com.alignment > 0 ? com.alignment : com.textMesh.alignment);
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
                                    (!string.IsNullOrEmpty(com.content) ? com.content : com.uiTextMesh.text));
                            }

                            com.color = EditorGUILayout.ColorField("颜色", com.color);
                            com.fontSize = EditorGUILayout.FloatField("大小",
                                (com.fontSize > 0 ? com.fontSize : com.uiTextMesh.fontSize));
                            com.lineSpacing = EditorGUILayout.FloatField("LineSpacing",
                                (com.lineSpacing > 0 ? com.lineSpacing : com.uiTextMesh.lineSpacing));
                            com.characterSpacing = EditorGUILayout.FloatField("CharacterSpacing",
                                (com.characterSpacing > 0 ? com.characterSpacing : com.uiTextMesh.characterSpacing));
                            
                            com.alignment = (TextAlignmentOptions)EditorGUILayout.EnumFlagsField(
                                "Alignment",
                                com.alignment > 0 ? com.alignment : com.uiTextMesh.alignment);
                            EditorGUI.indentLevel = 0;
                        }

                        if (EditorGUI.EndChangeCheck())
                        {
                            TrySave();
                        }
                    }
                }
                
                for (int i = 0; i < currentState.stateText.Count; i++)
                {
                    var com = currentState.stateText[i];
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
                                currentState.stateText.RemoveAt(i);
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
                            com.lineSpacing = EditorGUILayout.FloatField("LineSpacing",
                                (com.lineSpacing > 0 ? com.lineSpacing : com.text.lineSpacing));
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
                                    (!string.IsNullOrEmpty(com.content) ? com.content : com.uiText.text));
                            }

                            com.color = EditorGUILayout.ColorField("颜色", com.color);
                            com.fontSize = EditorGUILayout.IntField("大小",
                                (com.fontSize > 0 ? com.fontSize : com.uiText.fontSize));
                            com.lineSpacing = EditorGUILayout.FloatField("LineSpacing",
                                (com.lineSpacing > 0 ? com.lineSpacing : com.uiText.lineSpacing));
                            EditorGUI.indentLevel = 0;
                        }

                        if (EditorGUI.EndChangeCheck())
                        {
                            TrySave();
                        }
                    }
                }

                for (int i = 0; i < currentState.stateSelectable.Count; i++)
                {
                    var com = currentState.stateSelectable[i];
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
                                currentState.stateSelectable.RemoveAt(i);
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
                        
                        if (EditorGUI.EndChangeCheck())
                        {
                            TrySave();
                        }
                    }
                }
                
                for (int i = 0; i < currentState.stateCanvasGroups.Count; i++)
                {
                    var com = currentState.stateCanvasGroups[i];
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
                                currentState.stateCanvasGroups.RemoveAt(i);
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
                        
                        if (EditorGUI.EndChangeCheck())
                        {
                            TrySave();
                        }
                    }
                }
                
                for (int i = 0; i < currentState.stateGray.Count; i++)
                {
                    var com = currentState.stateGray[i];
                    com.foldout = EditorGUILayout.Foldout(com.foldout, com.name);
                    if (com.foldout)
                    {
                        EditorGUI.BeginChangeCheck();
                        using (new GUILayout.HorizontalScope())
                        {
                            com.go =
                                (GameObject)EditorGUILayout.ObjectField("组件", com.go, typeof(GameObject), true);
                            if (GUILayout.Button("删除"))
                            {
                                currentState.stateGray.RemoveAt(i);
                                i--;
                            }
                        }

                        if (com.go)
                        {
                            com.isGray = GUILayout.Toggle(com.isGray, "置灰");
                        }

                        if (EditorGUI.EndChangeCheck())
                        {
                            TrySave();
                        }
                    }
                }
                
                for (int i = 0; i < currentState.stateUIStates.Count; i++)
                {
                    var com = currentState.stateUIStates[i];
                    com.foldout = EditorGUILayout.Foldout(com.foldout, com.name);
                    if (com.foldout)
                    {
                        EditorGUI.BeginChangeCheck();
                        using (new GUILayout.HorizontalScope())
                        {
                            com.go =
                                (GameObject)EditorGUILayout.ObjectField("组件", com.go, typeof(GameObject), true);
                            if (GUILayout.Button("删除"))
                            {
                                currentState.stateUIStates.RemoveAt(i);
                                i--;
                            }
                        }

                        if (com.uiState)
                        {
                            var states = new List<string>();
                            foreach (var state in com.uiState.States)
                            {
                                states.Add(state.stateName);
                            }
                            
                            int selectedIndex = 0;
                            if (!string.IsNullOrEmpty(com.state))
                            {
                                selectedIndex = states.FindIndex(s => s == com.state);
                                if (selectedIndex == -1) selectedIndex = 0;
                            }
                            
                            if (states.Count > 0)
                            {
                                selectedIndex = EditorGUILayout.Popup("State", selectedIndex, states.ToArray());
                                com.state = states[selectedIndex];
                            }
                            else
                            {
                                EditorGUILayout.LabelField("State", "No states available");
                                com.state = "";
                            }
                        }

                        if (EditorGUI.EndChangeCheck())
                        {
                            TrySave();
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

                    foreach (var com in m_State.States[i].stateGray)
                    {
                        if (com.go)
                            hash.Add(com.go);
                    }

                    foreach (var com in m_State.States[i].stateUIStates)
                    {
                        if (com.go)
                            hash.Add(com.go);
                    }
                }

                m_State.Defaults.Clear();
                m_State.Defaults.AddRange(hash);
            }

            void TrySave()
            {
                SetDefault();
                m_State.SetState(stateIndex);
                EditorUtility.SetDirty(m_State);
            }
            
            List<T> CopyStateList<T>(List<T> list) where T : StateBase
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
        bool InProject(Object asset,out string assetName)
        {
            var path = AssetDatabase.GetAssetPath(asset);
            assetName = Path.GetFileName(path);
            return path.StartsWith("Assets");
        }
#endif
        public void Process()
        {
#if UNITY_EDITOR
            foreach (var state in States)
            {
                foreach (var images in state.stateImage)
                {
                    if (InProject(images.sprite,out var spriteName))
                    {
                        images.spriteName = spriteName; 
                    }
                    images.sprite = null;
                }
                foreach (var texts in state.stateTextMesh)
                {
                    if (InProject(texts.font,out var fontName))
                    {
                        texts.fontName = fontName; 
                    }
                    texts.font = null;
                    if (InProject(texts.material,out var materialName))
                    {
                        texts.materialName = materialName; 
                    }
                    texts.material = null;
                }
            }
#endif
        }
    }


}
