using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Umbreey;

public class InspectorView : VisualElement
{
    public new class UxmlFactory : UxmlFactory<InspectorView, UxmlTraits> { }
    
    public const int KPopupWidth = 70;
    private BehaviourTreeEditor _ownerWindow;
    private NodeBase _curNode;
    
    public void Init(BehaviourTreeEditor window)
    {
        _ownerWindow = window;
    }

    public void PopulateView(NodeBase node)
    {
        Clear();

        _curNode = node;
        if (_curNode != null)
        {
            IMGUIContainer imguiContainer = new IMGUIContainer(() =>
            {
                this.OnGUI();
            });
            Add(imguiContainer);
        }
        
    }
    
    private void OnGUI()
    {
        EditorGUILayout.Separator();
        EditorGUILayout.HelpBox($"{_curNode.GetType().Name}", MessageType.None);
        
        string description = EditorGUILayout.TextArea(_curNode.Description);
        if (description != _curNode.Description)
        {
            OnDescriptionChanged(description);
        }
        
        EditorGUILayout.Separator();
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        
        if (_curNode is SetVariableNode setVariableNode)
        {
            OnGUISetVariableNode(setVariableNode);
        }
        else
        {
            OnGUINodeCommon(_curNode);
        }
        
        EditorGUILayout.EndVertical();
    }

    private void OnGUISetVariableNode(SetVariableNode node)
    {
        var field = node.GetType().GetField("SetVariables", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var list = field.GetValue(node) as IList;
        
        foreach (var item in list)
        {
            var setVariable = item as SetVariable;
            
            var assignableVariables = _ownerWindow.Blackboard.Variables;
            var blackboardVariable = _ownerWindow.Blackboard.Get(setVariable.VariableName);

            if (blackboardVariable == null)
            {
                blackboardVariable = assignableVariables.FirstOrDefault();
                setVariable.Bind(blackboardVariable);
            }
            
            if (blackboardVariable != null)
            {
                EditorGUILayout.BeginHorizontal();
                
                var names = assignableVariables.Select(v => v.Name).ToList();
                int selectedIndex = assignableVariables.IndexOf(blackboardVariable);
                int newIndex = EditorGUILayout.Popup(selectedIndex, names.ToArray(), new GUILayoutOption[]
                {
                    GUILayout.Width(KPopupWidth)
                });
            
                if (newIndex != selectedIndex)
                {
                    setVariable.Bind(assignableVariables[newIndex]);
                    _ownerWindow.RecordUndo($"SetVariableNode variable changed");
                }
                
                BlackboardVariableDrawer.OnGUI(_ownerWindow, setVariable.Variable, "");
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.LabelField("No variables to display.");
            }
        }

        EditorGUILayout.Separator();
        EditorGUILayout.BeginHorizontal();
        {
            if (GUILayout.Button("添加"))
            {
                var setVariable = (SetVariable)Activator.CreateInstance(field.FieldType.GenericTypeArguments[0]);
                list.Add(setVariable);
            }
            
            if (GUILayout.Button("删除最后"))
            {
                if (list.Count > 0)
                {
                    list.RemoveAt(list.Count - 1);
                }
            }                
        }
        EditorGUILayout.EndHorizontal();
    }
    
    private void OnGUINodeCommon(NodeBase node)
    {
        var fields = node.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        
        // 单个变量
        var singleFields = fields.Where(f =>
            f.FieldType.IsSubclassOf(typeof(BlackboardVariableBase)) && 
            f.GetCustomAttribute<SerializeReference>() != null).ToArray();
        
        foreach (var field in singleFields)
        {
            var variable = field.GetValue(node) as BlackboardVariableBase;
            
            var newVariable = OnGUIVariableCommon(variable, field.Name);
            if (newVariable != null)
            {
                field.SetValue(node, newVariable);
            }
        }
        
        // 列表变量
        var listFields = fields.Where(f =>
            f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(List<>) &&
            f.FieldType.GenericTypeArguments[0].IsSubclassOf(typeof(BlackboardVariableBase))).ToArray();

        foreach (var field in listFields)
        {
            var list = field.GetValue(node) as IList;
            foreach (var item in list)
            {
                var variable = item as BlackboardVariableBase;
                
                var newVariable = OnGUIVariableCommon(variable, field.Name);
                if (newVariable != null)
                {
                    list[list.IndexOf(item)] = newVariable;
                }
            }

            EditorGUILayout.Separator();
            EditorGUILayout.BeginHorizontal();
            {
                if (GUILayout.Button("添加"))
                {
                    var newVariable = BlackboardVariableBase.Create(field.FieldType.GenericTypeArguments[0]);
                    newVariable.SetValue(Activator.CreateInstance(newVariable.GetValueType()));
   
                    list.Add(newVariable);
                }
                
                if (GUILayout.Button("删除最后"))
                {
                    if (list.Count > 0)
                    {
                        list.RemoveAt(list.Count - 1);
                    }
                }                
            }
            EditorGUILayout.EndHorizontal();
        }
        
        if (singleFields.Count() == 0 && listFields.Count() == 0)
        {
            EditorGUILayout.LabelField("No fields to display.");
        }
    }
    
    private BlackboardVariableBase OnGUIVariableCommon(BlackboardVariableBase variable, string name)
    {
        if (variable is BlackboardVariableCondition condition)
        {
            OnGUIVariableCondition(condition);
            return null;
        }
            
        var assignableVariables = GetAssignableVariables(_ownerWindow.Blackboard, variable.GetValueType());
        int selectedIndex = assignableVariables.IndexOf(variable);
            
        EditorGUILayout.BeginHorizontal();
        GUI.enabled = selectedIndex == -1;
        {
            BlackboardVariableDrawer.OnGUI(_ownerWindow, variable, name);
        }
        GUI.enabled = true;
            
        var newVariable = OnBlackboardVariablePopup(selectedIndex, assignableVariables, variable);
        EditorGUILayout.EndHorizontal();

        return newVariable;
    }
    
    private void OnGUIVariableCondition( BlackboardVariableCondition conditionVariable)
    {
        EditorGUILayout.BeginHorizontal();

        var condition = conditionVariable.Value;
        condition.OpCode = (Condition.Operator)EditorGUILayout.EnumPopup("", condition.OpCode, new GUILayoutOption[]
        {
            GUILayout.Width(KPopupWidth)
        });
        
        if (condition.OpCode is Condition.Operator.Set or Condition.Operator.NotSet)
        {
            var newName = EditorGUILayout.TextField("", condition.VariableName);
            if (newName != condition.VariableName)
            {
                condition.SetVariableName(newName);   
                _ownerWindow.RecordUndo($"Variable Condition VariableName changed");
            }
        }
        else
        {
            var allVariables = _ownerWindow.Blackboard.Variables;
            var variable = _ownerWindow.Blackboard.Get(condition.VariableName) ?? allVariables.FirstOrDefault();
            
            if (variable != null)
            {
                var names = allVariables.Select(v => v.Name).ToList();
                int selectedIndex = allVariables.IndexOf(variable);
                int newIndex = EditorGUILayout.Popup(selectedIndex, names.ToArray(), new GUILayoutOption[]
                {
                    GUILayout.Width(KPopupWidth)
                });
            
                if (newIndex != selectedIndex)
                {
                    variable = allVariables[newIndex];
                    condition.SetVariable(variable);
                    _ownerWindow.RecordUndo($"Variable Condition changed");
                }

                // ref variable
                if (condition.Variable == null)
                {
                    condition.SetVariable(variable);
                }
                
                var newRefVariable = OnGUIVariableCommon(condition.Variable, "");
                if (newRefVariable != null)
                {
                    condition.Variable = newRefVariable;
                }
            }
            else
            {
                EditorGUILayout.LabelField("No variables to display.");
            }
        }
        
        EditorGUILayout.EndHorizontal(); 
    }

    private static List<BlackboardVariableBase> GetAssignableVariables(Blackboard blackboard, Type valueType)
    {
        var variables = new List<BlackboardVariableBase>();
        foreach (var variable in blackboard.Variables)
        {
            if (variable.IsAssignableTo(valueType))
            {
                variables.Add(variable);
            }
        }

        return variables;
    }
    
    private void OnDescriptionChanged(string description)
    {
        _ownerWindow.RecordUndo($"Change Description {_curNode.GetType()}");
        _curNode.Description = description;

        foreach (var element in _ownerWindow.GraphView.graphElements)
        {
            if (element is NodeView nodeView)
            {
                if (nodeView.Node == _curNode)
                {
                    nodeView.SetDescription(description);
                }
            }
        }
    }
    
    private BlackboardVariableBase OnBlackboardVariablePopup(int selectedIndex, List<BlackboardVariableBase> blackboardVariables, BlackboardVariableBase nodeVariable)
    {
        var names = blackboardVariables.Select(v => v.Name).ToList();
        names.Insert(0, "自定义");
        selectedIndex++;
        
        int newIndex = EditorGUILayout.Popup(selectedIndex, names.ToArray(), new GUILayoutOption[]
        {
            GUILayout.MaxWidth(KPopupWidth)
        });
        
        if (newIndex != selectedIndex)
        {
            _ownerWindow.RecordUndo($"Variable popup changed");
            if (newIndex == 0)
            {
                var customVariable = nodeVariable.Clone();
                return customVariable;
            }
            else
            {
                return blackboardVariables[newIndex - 1];
            }
        }

        return null;
    }
}
