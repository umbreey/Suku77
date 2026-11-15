using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;
using Umbreey;


public class BlackboardView : UnityEditor.Experimental.GraphView.Blackboard
{
    private BehaviourTreeEditor _ownerWindow;
    private Umbreey.Blackboard _blackboard;
    
    public BlackboardView(UnityEditor.Experimental.GraphView.GraphView associatedGraphView) : base(associatedGraphView)
    {
        var ussPath = BehaviourTreeEditor.GetUSSPath("BehaviourTreeBlackboardView");
        var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(ussPath);
        styleSheets.Add(styleSheet);
        
        title = "黑板";
        subTitle = null;
        
        style.width = 250;
        style.height = 300;
    }

    public void Init(BehaviourTreeEditor window)
    {
        if (_ownerWindow == window)
            return;
        
        _ownerWindow = window;
        window.GraphView.Add(this);
        
        addItemRequested += OnAddClicked;
        editTextRequested += OnEditClicked;
    }

    public void PopulateView(Umbreey.Blackboard blackboard)
    {
        this.Clear();
        
        _blackboard = blackboard;
        if (_blackboard == null)
            return;
        
        ScrollView scrollView = new ScrollView();
        Add(scrollView);
        
        foreach (var variable in _blackboard.Variables)
        {
            var field = new BlackboardFieldView(this) { text = variable.Name, typeText = variable.GetValueType().Name};
            var imguiContainer = new IMGUIContainer(() =>
            {
                BlackboardVariableDrawer.OnGUI(_ownerWindow, variable, "Value");
            });
            
            scrollView.Add(new BlackboardRow(field, imguiContainer));
        }
    }
    
    private void OnAddClicked(UnityEditor.Experimental.GraphView.Blackboard self)
    {
        var variableTypes = TypeCache.GetTypesDerivedFrom<BlackboardVariableBase>().ToList();
        variableTypes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        
        var paramType = new GenericMenu();
        foreach (Type variableType in variableTypes)
        {
            var valueField = BlackboardVariableBase.InternalValueFieldInfo(variableType);
            if (valueField != null)
            {
                var valueType = valueField.FieldType;
                paramType.AddItem(new GUIContent(valueType.Name), false, () =>
                {
                    string name = $"New {valueType.Name}";
                    while (_blackboard.IsSet(name))
                    {
                        name = $"{name}(1)";
                    }

                    if (valueType.IsAbstract)
                    {
                        _blackboard.SetVariable(name, variableType, null);
                    }
                    else
                    {
                        var defaultValue = valueType == typeof(string) ? "" : Activator.CreateInstance(valueType);
                        _blackboard.SetValue(name, defaultValue);
                    }
                    
                    PopulateView(_blackboard);
                });                
            }
            

        }
        paramType.ShowAsContext();
    }
    
    private void OnEditClicked(UnityEditor.Experimental.GraphView.Blackboard self, VisualElement element, string newName)
    {
        var field = element as BlackboardField;
        var oldName = field.text;
        if (_blackboard.IsSet(newName))
        {
            EditorUtility.DisplayDialog("Error", "变量名字重复了", "OK");
            return;
        }
        
        _blackboard.Rename(oldName, newName);
        field.text = newName;
    }
    
    public void OnRemoveClicked(BlackboardFieldView field)
    {
        var name = field.text;
        _blackboard.UnsetValue(name);
        
        PopulateView(_blackboard);
    }
}
