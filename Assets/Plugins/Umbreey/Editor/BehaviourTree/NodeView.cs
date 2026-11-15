using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;
using Umbreey;

public class NodeView : Node
{
    public NodeBase Node { get; private set; }
    public Port InputPort { get; private set; }
    public Port OutputPort { get; private set; }
    
    private Label _descriptionLabel;
    private BehaviourTreeEditor _ownerWindow;
    
    public NodeView(NodeBase node, BehaviourTreeEditor window) 
        : base(BehaviourTreeEditor.GetUXMLPath("NodeView"))
    {
        _ownerWindow = window;
        Node = node;
        
        title = node.GetType().Name.Replace("Node", "");
        style.left = node.Position.x;
        style.top = node.Position.y;
        style.minWidth = 100;
        
        _descriptionLabel = this.Q<Label>("description");
        SetDescription(node.Description);
        
        InitPortViews();
        AddNodeTypeClass();
    }

    public void SetDescription(string desc)
    {
        _descriptionLabel.text = desc;
    }

    public void InitPortViews()
    {
        if (!(Node is RootNode))
        {
            InputPort = NodePortView.Create(Orientation.Vertical, Direction.Input, Port.Capacity.Single, typeof(NodePortView));
            InputPort.portName = null;
            InputPort.style.flexDirection = FlexDirection.Column;
            inputContainer.Add(InputPort);   
        }

        if (!(Node is ActionNode))
        {
            var capacity = Node is DecoratorNode ? Port.Capacity.Single : Port.Capacity.Multi;
            OutputPort = NodePortView.Create(Orientation.Vertical, Direction.Output, capacity, typeof(NodePortView));
            OutputPort.portName = null;
            OutputPort.style.flexDirection = FlexDirection.ColumnReverse;
            outputContainer.Add(OutputPort);
        }
    }
    
    public void AddChildView(NodeView childNodeView)
    {
        if (Node is ContainerNode cNode)
        {
            _ownerWindow.RecordUndo($"AddChild {childNodeView.Node.GetType()}");
            cNode.AddChild(childNodeView.Node);
            
            var graphView = _ownerWindow.GraphView;
            if (childNodeView.InputPort.connected)
            {
                var edge = childNodeView.InputPort.connections.First();
                edge.output.Disconnect(edge);
                graphView.RemoveElement(edge);
            }

            if (OutputPort.capacity == Port.Capacity.Single && OutputPort.connected)
            {
                var edge = OutputPort.connections.First();
                edge.output.Disconnect(edge);
                graphView.RemoveElement(edge);
            }
            
            graphView.AddElement(OutputPort.ConnectTo(childNodeView.InputPort));
        }
        else
        {
            Debug.LogError($"Can't add child to node type: {Node.GetType()}");
        }
    }

    public void RemoveChildView(NodeView childNodeView)
    {
        if (Node is ContainerNode cNode)
        {
            _ownerWindow.RecordUndo($"RemoveChild {childNodeView.Node.GetType()}");
            cNode.RemoveChild(childNodeView.Node);
            
            var edge = childNodeView.InputPort.connections.First();
            OutputPort.Disconnect(edge);
            _ownerWindow.GraphView.RemoveElement(edge);
        }
        else
        {
            Debug.LogError($"Can't remove child from node type: {Node.GetType()}");
        }
    }
    
    public override void SetPosition(Rect newPos)
    {
        _ownerWindow.RecordUndo($"SetPosition {Node.GetType()}");
        
        base.SetPosition(newPos);
        Node.Position = new Vector2(newPos.left, newPos.top);
    }

    public override void OnSelected()
    {
        base.OnSelected();
        
        _ownerWindow.OnNodeSelected(this.Node);
    }
    
    public void UpdateRuntimeState()
    {
        RemoveFromClassList("running");
        RemoveFromClassList("success");
        RemoveFromClassList("failure");
        RemoveFromClassList("anystate");

        switch (Node.CurState)
        {
            case NodeBase.State.Running:
                AddToClassList("running");
                AddToClassList("anystate");
                break;
            case NodeBase.State.Success:
                AddToClassList("success");
                AddToClassList("anystate");
                break;
            case NodeBase.State.Failure:
                AddToClassList("failure");
                AddToClassList("anystate");
                break;
            case NodeBase.State.None:
                break;
        }
    }
    
    private void AddNodeTypeClass()
    {
        if (Node is RootNode)
        {
            AddToClassList("root");
        }
        else if (Node is ActionNode)
        {
            AddToClassList("action");
        }
        else if (Node is DecoratorNode)
        {
            AddToClassList("decorator");
        }
        else if (Node is CompositeNode)
        {
            AddToClassList("composite");
        }
    }
}
