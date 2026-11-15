using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;
using Umbreey;

public class NodePortView : Port
{
    protected NodePortView(Orientation portOrientation, Direction portDirection, Capacity portCapacity, System.Type type) : 
        base(portOrientation, portDirection, portCapacity, type)
    {
    }
    
    public static NodePortView Create(Orientation orientation, Direction direction, Capacity capacity, System.Type type)
    {
        NodeEdgeConnectorListener listener = new NodeEdgeConnectorListener();
        var ele = new NodePortView(orientation, direction, capacity, type);
        ele.m_EdgeConnector = new EdgeConnector<Edge>(listener);
        ele.AddManipulator(ele.m_EdgeConnector);
        return ele;
    }
}

public class NodeEdgeConnectorListener : IEdgeConnectorListener
{
    public void OnDrop(GraphView graphView, Edge edge)
    {
        var outputNodeView = edge.output.node as NodeView;
        var inputNodeView = edge.input.node as NodeView;
        
        outputNodeView.AddChildView(inputNodeView);
    }

    public void OnDropOutsidePort(Edge edge, Vector2 position)
    {
        var ownerWindow = (BehaviourTreeEditor)EditorWindow.focusedWindow;
        
        var searchWindow = ScriptableObject.CreateInstance<NodeSearchWindowProvider>();
        searchWindow.Init(ownerWindow, edge);
        
        var screenMousePosition = position + ownerWindow.position.position;
        SearchWindow.Open(new SearchWindowContext(screenMousePosition), searchWindow);
    }
}
