using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Umbreey;

public class BlackboardEditorHelper
{
    public static void DrawInspector(Blackboard blackboard,UnityEngine.GameObject so)
    {
        if(blackboard == null || so==null)return;
        var uiLang = so.GetComponent<UILangLibrary>();
        
        EditorGUILayout.BeginVertical();
        for(int i=0;i<blackboard.Variables.Count;i++)
        {
            var variable = blackboard.Variables[i];
            EditorGUILayout.BeginVertical(new GUIStyle(GUI.skin.button));
            string langText = uiLang?.GetTextEditor(variable.Name)??variable.Name;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"{langText} ({variable.GetValueType().Name})");
            if (i>0 && GUILayout.Button("上移",GUILayout.Width(50)))
            {
                blackboard.MoveUp(variable.Name);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            string newName= EditorGUILayout.TextField("", variable.Name, GUILayout.Width(100), GUILayout.ExpandWidth(true));
            if (!string.IsNullOrEmpty(newName) && variable.Name!=newName)
            {
                if (blackboard.IsSet(newName))
                {
                    EditorUtility.DisplayDialog("Error", "变量名字重复了", "OK");
                    return;
                }
                blackboard.Rename(variable.Name, newName);
            }
            BlackboardVariableDrawer.OnGUI(null, variable, "");
            if (GUILayout.Button("-",GUILayout.Width(20)))
            {
                blackboard.UnsetValue(variable.Name);
                break;
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }
        EditorGUILayout.EndVertical();
        
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("添加"))
        {
            OnAddClicked(blackboard);
        }
        
        if (GUILayout.Button("导出"))
        {
            string json = BlackboardSerializeUtil.Serialize(blackboard);
            Debug.Log(json);
        }
        EditorGUILayout.EndHorizontal();
    
        // 如果你对 scriptObject 做了任何更改，确保调用这个方法
        if (GUI.changed)
        {
            EditorUtility.SetDirty(so);
        }
    }
    
    private static void OnAddClicked(Blackboard blackboard)
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
                    while (blackboard.IsSet(name))
                    {
                        name = $"{name}(1)";
                    }

                    System.Object defaultValue;
                    if (valueType.IsSubclassOf(typeof(UnityEngine.Object)))
                    {
                        defaultValue = default(UnityEngine.Object);
                    }
                    else
                    {
                        defaultValue = valueType == typeof(string) ? "" : Activator.CreateInstance(valueType);
                    }
                    blackboard.SetVariable(name, variableType, defaultValue);
                });                
            }
        }
        paramType.ShowAsContext();
    }
}