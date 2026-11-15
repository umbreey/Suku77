using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Umbreey;
using Object = UnityEngine.Object;

public class BehaviourTreeEditor : EditorWindow
{
    public static Func<UnityEngine.Object, BehaviourTree> extGetRuntimeBehaviourTree;
    
    [SerializeField]
    private VisualTreeAsset m_VisualTreeAsset = default;

    #region Visual
    public InspectorView InspectorView { get; private set; }
    public BehaviourTreeGraphView GraphView { get; private set; }
    public BlackboardView BlackboardView { get; private set; }

    private ObjectField _assetObjField;
    private ToolbarMenu _toolbarMenu;
    #endregion

    #region Editing
    public BehaviourTree BehaviourTree { get; private set; }
    public Blackboard Blackboard => BehaviourTree.Blackboard;

    private BehaviourTreeScriptObject _originalBTSO
    {
        get => _assetObjField.value as BehaviourTreeScriptObject;
        set => _assetObjField.value = value;
    }
    
    private delegate void GetUndoListDelegate(List<string> undoList, out int index);
    private static MethodInfo GetUndoListMethod = typeof(Undo).GetMethod("GetUndoList", BindingFlags.NonPublic | BindingFlags.Static);
    private static GetUndoListDelegate GetUndoList = (GetUndoListDelegate)Delegate.CreateDelegate(typeof(GetUndoListDelegate), GetUndoListMethod);
    
    private BehaviourTreeScriptObject _undoObj;
    private bool _isRuntime;
    private bool _isModify;
    #endregion

    #region USS / UXML
    private const string KConfigurationRoot = "Assets/Plugins/Umbreey/Editor/BehaviourTree";
    public static string GetUSSPath(string name)
    {
        return $"{KConfigurationRoot}/USS/{name}.uss";
    }
    
    public static string GetUXMLPath(string name)
    {
        return $"{KConfigurationRoot}/UXML/{name}.uxml";
    }
    #endregion

    [MenuItem("Umbreey/BehaviourTree Editor")]
    public static void ShowWindow()
    {
        BehaviourTreeEditor wnd = GetWindow<BehaviourTreeEditor>();
        wnd.titleContent = new GUIContent("BehaviourTree Editor");
    }
    
    [OnOpenAsset]
    public static bool OnOpenAsset(int instanceID, int line)
    {
        var obj = EditorUtility.InstanceIDToObject(instanceID);
        if (obj is BehaviourTreeScriptObject)
        {
            ShowWindow();
            return true;
        }

        return false;
    }

    public void CreateGUI()
    {
        m_VisualTreeAsset.CloneTree(rootVisualElement);
        CreateToolbar();
        
        InspectorView = rootVisualElement.Q<InspectorView>();
        InspectorView.Init(this);
        
        GraphView = rootVisualElement.Q<BehaviourTreeGraphView>();
        GraphView.Init(this);

        BlackboardView = new BlackboardView(GraphView);
        BlackboardView.Init(this);
        
        OnSelectionChange();
        if (_originalBTSO == null)
        {
            New();
        }
        
        Undo.ClearAll();
        Undo.undoRedoPerformed += OnUndoRedo;
    }

    private void OnEnable()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private void OnDestroy()
    {
        DestroyUndoObject();
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        Undo.undoRedoPerformed -= OnUndoRedo;
    }

    #region Selection
    private void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        switch (state)
        {
            case PlayModeStateChange.EnteredEditMode:
                OnSelectionChange();
                break;
            case PlayModeStateChange.ExitingEditMode:
                break;
            case PlayModeStateChange.EnteredPlayMode:
                break;
            case PlayModeStateChange.ExitingPlayMode:
                break;
            
        }
    }
    public void OnNodeSelected(NodeBase node)
    {
        InspectorView.PopulateView(node);
    }
    
    private void OnSelectionChange()
    {
        var selected = Selection.activeObject;
        if (selected != null && selected != _originalBTSO && // switch tree
            _isModify && !_isRuntime) // unsaved changes
        {
            if (EditorUtility.DisplayDialog("Tip", "Save current behaviour tree?", "Save", "Cancel"))
            {
                Save();
            }
        }
        
        var btRuntime = extGetRuntimeBehaviourTree?.Invoke(selected);
        if (btRuntime != null)
        {
            _isRuntime = true;
            BehaviourTree = btRuntime;
            var allNodes = BehaviourTree.GetAllNodes();
            
            CreateUndoObject(allNodes);
            Populate(allNodes);
        } 
        else if (selected is BehaviourTreeScriptObject btso)
        {
            _isRuntime = false;
            _originalBTSO = btso;

            List<NodeBase> allNodes = new List<NodeBase>();
            BehaviourTree = btso.CreateBehaviourTree(allNodes);
            
            CreateUndoObject(allNodes);
            Populate(allNodes);
        }
        else if (selected is GameObject obj && obj.GetComponent<BehaviourTreeComponent>() != null)
        {
            var comp = obj.GetComponent<BehaviourTreeComponent>();
            _originalBTSO = comp.BehaviourTreeAsset;
            
            List<NodeBase> allNodes = new List<NodeBase>();
            if (Application.isPlaying)
            {
                _isRuntime = true;
                BehaviourTree = comp.BehaviourTree;
                allNodes = BehaviourTree.GetAllNodes();
            }
            else
            {
                _isRuntime = false;
                BehaviourTree = comp.BehaviourTreeAsset.CreateBehaviourTree(allNodes);
            }
            
            CreateUndoObject(allNodes);
            Populate(allNodes);
        }
        else if (selected != null)
        {
            _isRuntime = false;
            _originalBTSO = null;
            
            DestroyUndoObject();
            Populate(null);
        }
        
        LoadEditorSettings(_originalBTSO);
    }

    private void Update()
    {
        if (Application.isPlaying)
        {
            GraphView.UpdateNodeViews();
        }
    }

    private void Populate(List<NodeBase> allNodes)
    {
        GraphView.PopulateView(allNodes);
        BlackboardView.PopulateView(allNodes?.FirstOrDefault()?.GetBlackboard());
        InspectorView.PopulateView(null);
    }
    #endregion
    
    #region Undo
    private void CreateUndoObject(List<NodeBase> allNodes)
    {
        DestroyUndoObject();
        
        _undoObj = ScriptableObject.CreateInstance<BehaviourTreeScriptObject>();
        _undoObj.SetAll(allNodes);
    }
    
    protected void DestroyUndoObject()
    {
        _isModify = false;
        Undo.ClearAll();
        DestroyImmediate(_undoObj);
    }
    
    private void OnUndoRedo()
    {
        if (BehaviourTree == null)
            return;

        List<string> undoList = new List<string>();
        GetUndoList(undoList, out int index);
        _isModify = index != -1;
        
        Populate(_undoObj.AllNodes);
    }
    
    public void RecordUndo(string name)
    {
        if (BehaviourTree == null)
            return;

        _isModify = true;
        _undoObj.SetAll(GraphView.GetAllNodes());
        Undo.RecordObject(_undoObj, $"BehaviourTree {name}");
    }
    #endregion
    
    #region Toolbar
    private void CreateToolbar()
    {
        _assetObjField = rootVisualElement.Q<ObjectField>();
        _toolbarMenu = rootVisualElement.Q<ToolbarMenu>();

        _assetObjField.SetEnabled(false);
        
        _toolbarMenu.menu.AppendAction("New", (a) =>
        {
            New();
        });
        _toolbarMenu.menu.AppendAction("Save", (a) =>
        {
            Save();
        });
        _toolbarMenu.menu.AppendAction("Save As", (a) =>
        {
            SaveAs();
        });
    }
    
    private void New()
    {
        Selection.activeObject = null;
        _originalBTSO = null;

        var bt = new BehaviourTree();
        bt.RootNode.Position = new Vector2(400, 100);

        BehaviourTree = bt;
        var allNodes = bt.GetAllNodes();
        
        CreateUndoObject(allNodes);
        Populate(allNodes);
    }

    private void Save()
    {
        if (BehaviourTree == null)
            return;
        
        if (_originalBTSO == null)
        {
            SaveAs();
            return;
        }

        var allNodes = GraphView.GetAllNodes();
        var btso = _originalBTSO;
        btso.SetAll(allNodes);
        CreateUndoObject(allNodes);
        SaveEditorSettings(btso);
        
        string path = AssetDatabase.GetAssetPath(btso);
        if (AssetDatabase.Contains(btso))
        {
            EditorUtility.SetDirty(btso);
            AssetDatabase.SaveAssets();
        }
        else
        {
            AssetDatabase.CreateAsset(btso, path);
        }
        AssetDatabase.Refresh();
        EditorUtility.DisplayDialog("Tip", "Behaviour tree saved", "OK");
        
        // Select the saved asset
        Selection.activeObject = btso;
    }

    private void SaveAs()
    {
        if (BehaviourTree == null)
            return;
        
        string path = EditorUtility.SaveFilePanelInProject("SaveAs", "NewBehaviourTree", "asset", "Save behaviour tree");
        if (string.IsNullOrEmpty(path))
        {
            Debug.Log("Save cancelled");
            return;
        }
        
        var allNodes = GraphView.GetAllNodes();
        var btso = ScriptableObject.CreateInstance<BehaviourTreeScriptObject>();
        btso.SetAll(allNodes);
        CreateUndoObject(allNodes);
        SaveEditorSettings(btso);
        
        AssetDatabase.CreateAsset(btso, path);
        AssetDatabase.Refresh();
        
        EditorUtility.DisplayDialog("Tip", "Behaviour tree saved", "OK");
        
        // Select the saved asset
        Selection.activeObject = btso;
    }

    private void LoadEditorSettings(BehaviourTreeScriptObject btso)
    {
        if (btso == null)
            return;

        if (btso.InspectorViewWidth > 0)
        {
            rootVisualElement.Q<SplitView>().fixedPaneInitialDimension = btso.InspectorViewWidth;
        }
        if (btso.GraphViewRect.width > 0)
        {
            GraphView.viewTransform.position = btso.GraphViewRect.position;
            GraphView.viewTransform.scale = btso.GraphViewRect.size;
        }
        if (btso.BlackboardViewRect.width > 0)
        {
            BlackboardView.SetPosition(btso.BlackboardViewRect);
        }
    }
	
    private void SaveEditorSettings(BehaviourTreeScriptObject btso)
    {
        if (btso == null)
            return;

        btso.InspectorViewWidth = rootVisualElement.Q<VisualElement>("right-panel").layout.width;
        btso.GraphViewRect = new Rect(GraphView.viewTransform.position, GraphView.viewTransform.scale);
        btso.BlackboardViewRect = BlackboardView.GetPosition();
    }
    #endregion
    

    
    
}
