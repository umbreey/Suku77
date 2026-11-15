using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TreeEditor;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;
using Umbreey;

public class NodeSearchWindowProvider : ScriptableObject, ISearchWindowProvider
{
    private BehaviourTreeEditor _ownerWindow;
    
    private NodeView _sourceNode;
    private bool _sourceIsOutput;
    
    public void Init(BehaviourTreeEditor window, Edge edge = null)
    {
        _ownerWindow = window;

        if (edge != null)
        {
            if (edge.output != null)
            {
                _sourceNode = edge.output.node as NodeView;
                _sourceIsOutput = true;
            }
            else
            {
                _sourceNode = edge.input.node as NodeView;
                _sourceIsOutput = false;
            }
        }
    }
    
    public List<SearchTreeEntry> CreateSearchTree(SearchWindowContext context)
    {
        var entries = new List<SearchTreeEntry>();
        entries.Add(new SearchTreeGroupEntry(new GUIContent("行为树节点"), 0));
        
        entries.Add(new SearchTreeGroupEntry(new GUIContent("复合节点"), 1));
        AddEntriesDerivedFrom<CompositeNode>(entries, 2);
        
        entries.Add(new SearchTreeGroupEntry(new GUIContent("装饰节点"), 1));
        AddEntriesDerivedFrom<DecoratorNode>(entries, 2);
        
        entries.Add(new SearchTreeGroupEntry(new GUIContent("动作节点"), 1));
        AddEntriesDerivedFrom<ActionNode>(entries, 2);
        
        return entries;
    }

    public bool OnSelectEntry(SearchTreeEntry SearchTreeEntry, SearchWindowContext context)
    {
        var nodeType = SearchTreeEntry.userData as Type;
        NodeBase node = _ownerWindow.BehaviourTree.CreateNode(nodeType);
        var nodeView = new NodeView(node, _ownerWindow);
        
        // set nodeView to current screen pos
        var windowMousePosition = _ownerWindow.rootVisualElement.ChangeCoordinatesTo(
            _ownerWindow.GraphView.contentViewContainer, context.screenMousePosition - _ownerWindow.position.position);
        nodeView.SetPosition(new Rect(windowMousePosition, Vector2.zero));
        
        var graphView = _ownerWindow.GraphView;
        graphView.AddElement(nodeView);
        
        _ownerWindow.RecordUndo($"CreateNode {node.GetType()}");
        if (_sourceNode != null)
        {
            if (_sourceIsOutput)
            {
                _sourceNode.AddChildView(nodeView);
            }
            else if (nodeView.OutputPort != null)
            {
                nodeView.AddChildView(_sourceNode);
            }
        }
        
        return true;
    }
    
    private void AddEntriesDerivedFrom<T>(List<SearchTreeEntry> entries, int level) where T : NodeBase
    {
        var nodeTypes = TypeCache.GetTypesDerivedFrom<T>().ToList();
        nodeTypes = nodeTypes.Where(t => !t.IsAbstract && t != typeof(RootNode)).ToList();
        nodeTypes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

        foreach (var t in nodeTypes)
        {
            var name = t.Name.Replace("Node", "");
            var guiContent = new GUIContent($"    {name}");
            var searchTreeEntry = new SearchTreeEntry(guiContent)
            {
                level = level,
                userData = t
            };
            entries.Add(searchTreeEntry);
        }
    }
}
