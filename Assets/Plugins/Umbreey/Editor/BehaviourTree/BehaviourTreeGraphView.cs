using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;
using Umbreey;

public class BehaviourTreeGraphView : GraphView
{
    public new class UxmlFactory : UxmlFactory<BehaviourTreeGraphView, UxmlTraits> { }
    
    private BehaviourTreeEditor _ownerWindow;
    
    public BehaviourTreeGraphView()
    {
        Insert(0, new GridBackground());
        SetupZoom(ContentZoomer.DefaultMinScale, ContentZoomer.DefaultMaxScale);
        this.AddManipulator(new ContentZoomer());
        this.AddManipulator(new ContentDragger());
        this.AddManipulator(new SelectionDragger());
        this.AddManipulator(new RectangleSelector());

        var ussPath = BehaviourTreeEditor.GetUSSPath("BehaviourTreeGraphView");
        var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(ussPath);
        styleSheets.Add(styleSheet);

        serializeGraphElements = SerializeGraphElementsCallback;
        canPasteSerializedData = CanPasteSerializedDataCallback;
        unserializeAndPaste = UnserializeAndPasteCallback;
    }
    
    public void Init(BehaviourTreeEditor window)
    {
        _ownerWindow = window;
        
        var searchWindow = ScriptableObject.CreateInstance<NodeSearchWindowProvider>();
        searchWindow.Init(window);
        
        nodeCreationRequest += (context) =>
        {
            SearchWindow.Open(new SearchWindowContext(context.screenMousePosition), searchWindow);
        };
    }
    
    public void PopulateView(List<NodeBase> allNodes)
    {
        graphViewChanged -= OnGraphViewChanged;
        DeleteElements(graphElements);
        graphViewChanged += OnGraphViewChanged;
        
        if (allNodes == null)
            return;
        
        // create nodes
        var nodeDict = new Dictionary<NodeBase, NodeView>();
        foreach (var node in allNodes)
        {
            if (node == null) continue;
            
            var nodeView = new NodeView(node, _ownerWindow);
            nodeDict.Add(node, nodeView);
            
            AddElement(nodeView);
        }
        
        // create edges
        foreach (var node in allNodes)
        {
            if (node != null && node.Parent != null)
            {
                var outputNodeView = nodeDict[node.Parent];
                var inputNodeView = nodeDict[node];
                
               var edge = outputNodeView.OutputPort.ConnectTo(inputNodeView.InputPort);
               AddElement(edge);
            }
        }
    }
    
    public List<NodeBase> GetAllNodes()
    {
        var nodes = new List<NodeBase>();
        foreach (var element in graphElements)
        {
            if (element is NodeView nodeView)
            {
                nodes.Add(nodeView.Node);
            }
        }
        
        return nodes;
    }
    
    public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter)
    {
        var compatiblePorts = new List<Port>();
        ports.ForEach((port) =>
        {
            if (startPort != port && startPort.node != port.node)
            {
                compatiblePorts.Add(port);
            }
        });

        return compatiblePorts;
    }

    private GraphViewChange OnGraphViewChanged(GraphViewChange change)
    {
        if (change.elementsToRemove != null)
        {
            foreach (var element in change.elementsToRemove)
            {
                if (element is NodeView nodeView)
                {
                    if (nodeView.Node is RootNode rootNode)
                    {
                        EditorUtility.DisplayDialog("提示", "根节点不能删除", "确定");
                        Undo.PerformUndo();
                    }
                    else
                    {
                        _ownerWindow.RecordUndo($"RemoveNode {nodeView.Node.GetType()}");
                        _ownerWindow.BehaviourTree.RemoveNode(nodeView.Node);                        
                    }
                }
                else if (element is Edge edge)
                {
                    var outputNodeView = edge.output.node as NodeView;
                    var inputNodeView = edge.input.node as NodeView;

                    outputNodeView.RemoveChildView(inputNodeView);
                }
            }
        }

        if (change.movedElements != null)
        {
            foreach (var element in change.movedElements)
            {
                if (element is NodeView nodeView)
                {
                    if (nodeView.Node.Parent is CompositeNode pNode)
                    {
                        _ownerWindow.RecordUndo($"SortNode {pNode.GetType()}");
                        pNode.SortOrders(((a, b) =>
                        {
                            return a.Position.x < b.Position.x ? -1 : 1;
                        }));
                    }
                }
            }
        }
        
        return change;
    }

    public void UpdateNodeViews()
    {
        foreach (var element in graphElements)
        {
            if (element is NodeView nodeView)
            {
                nodeView.UpdateRuntimeState();
            }
        }
    }

    #region Copy / Paste
    public class CopyPasteData
    {
        [Serializable]
        public class CopyPasteNode
        {
            public string NodeType;
            public string NodeData;
        }
        public List<CopyPasteNode> CopiedNodes = new List<CopyPasteNode>();
    }
    
    private string SerializeGraphElementsCallback(IEnumerable<GraphElement> elements)
    {
        CopyPasteData copyData = new CopyPasteData();
        foreach (var element in elements)
        {
            if (element is NodeView nodeView)
            {
                if (nodeView.Node is RootNode)
                    continue;

                var json = JsonUtility.ToJson(nodeView.Node);
                copyData.CopiedNodes.Add(new CopyPasteData.CopyPasteNode()
                {
                    NodeType = nodeView.Node.GetType().ToString(),
                    NodeData = json
                });
            }
        }
        
        return JsonUtility.ToJson(copyData);
    }


    private bool CanPasteSerializedDataCallback(string data)
    {
        try
        {
            var copyData = JsonUtility.FromJson<CopyPasteData>(data);
            return copyData.CopiedNodes.Count > 0;
        }
        catch (Exception e)
        {
            return false;
        }
    }
    private void UnserializeAndPasteCallback(string operationName, string data)
    {
        ClearSelection();
        
        var copyData = JsonUtility.FromJson<CopyPasteData>(data);
        _ownerWindow.RecordUndo("Paste Nodes-Pre");
        
        foreach (var copyNode in copyData.CopiedNodes)
        {
            var assembly = Assembly.GetAssembly(typeof(NodeBase));
            Type nodeType = assembly.GetType(copyNode.NodeType);
            
            var node = JsonUtility.FromJson(copyNode.NodeData, nodeType) as NodeBase;
            node.Position += new Vector2(50, 50);
            node.SetParent(null);
            node.SetBlackboard(_ownerWindow.Blackboard);
            
            var nodeView = new NodeView(node, _ownerWindow);
            AddElement(nodeView);
            AddToSelection(nodeView);
            
            // link blackboard variables
            var fields = node.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            fields = fields.Where(f => f.FieldType.IsSubclassOf(typeof(BlackboardVariableBase))).ToArray();

            foreach (var field in fields)
            {
                var variable = field.GetValue(node) as BlackboardVariableBase;
                var blackboardVariable = _ownerWindow.Blackboard.Get(variable.Name);
                if (blackboardVariable != null)
                {
                    field.SetValue(node, blackboardVariable);
                }
            }
        }
        
        _ownerWindow.RecordUndo($"Paste Nodes");
    }
    

    #endregion
}
