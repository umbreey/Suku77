using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Umbreey;

public class BlackboardVariableDrawer
{
    public static void OnGUI(BehaviourTreeEditor ownerWindow, BlackboardVariableBase variable, string overrideName = "")
    {
        if (variable == null)
            return;
        
        var name = overrideName != null ? overrideName : variable.Name;
        
        if (variable is BBoardVarBool boolVariable)
        {
            bool newValue = EditorGUILayout.Toggle(name, boolVariable.Value,GUILayout.MinWidth(100));
            CheckVariableDirty(ownerWindow, newValue, boolVariable.Value);
            
            boolVariable.SetValue(newValue); 
        }
        else if (variable is BBoardVarInt intVariable)
        {
            int newValue = EditorGUILayout.IntField(name, intVariable.Value,GUILayout.MinWidth(100));
            CheckVariableDirty(ownerWindow, newValue, intVariable.Value);
            
            intVariable.SetValue(newValue);
        }
        else if (variable is BBoardVarUInt uintVariable)
        {
            int newValue = EditorGUILayout.IntField(name, (int)uintVariable.Value,GUILayout.MinWidth(100));
            newValue = Math.Max(newValue, 0);
            CheckVariableDirty(ownerWindow, (uint)newValue, uintVariable.Value);
            
            uintVariable.SetValue((uint)newValue);
        }
        else if (variable is BBoardVarFloat floatVariable)
        {
            float newValue = EditorGUILayout.FloatField(name, floatVariable.Value,GUILayout.MinWidth(100));
            CheckVariableDirty(ownerWindow, newValue, floatVariable.Value);
            
            floatVariable.SetValue(newValue);
        }
        else if (variable is BBoardVarString stringVariable)
        {
            string newValue = EditorGUILayout.TextField(name, stringVariable.Value,GUILayout.MinWidth(100));
            CheckVariableDirty(ownerWindow, newValue, stringVariable.Value);

            stringVariable.SetValue(newValue);
        }
        else if (variable is BBoardVarVector2 vector2Variable)
        {
            Vector2 newValue = EditorGUILayout.Vector2Field(name, vector2Variable.Value,GUILayout.MinWidth(100));
            CheckVariableDirty(ownerWindow, newValue, vector2Variable.Value);
            
            vector2Variable.SetValue(newValue);
        }
        else if (variable is BBoardVarVector3 vector3Variable)
        {
            Vector3 newValue = EditorGUILayout.Vector3Field(name, vector3Variable.Value,GUILayout.MinWidth(100));
            CheckVariableDirty(ownerWindow, newValue, vector3Variable.Value);
            
            vector3Variable.SetValue(newValue);
        }
        else if (variable is BBoardVarVector4 vector4Variable)
        {
            Vector4 newValue = EditorGUILayout.Vector4Field(name, vector4Variable.Value,GUILayout.MinWidth(100));
            CheckVariableDirty(ownerWindow, newValue, vector4Variable.Value);
            
            vector4Variable.SetValue(newValue);
        }
        else if (variable is BBoardVarColor colorVariable)
        {
            Color newValue = EditorGUILayout.ColorField(name, colorVariable.Value,GUILayout.MinWidth(100));
            CheckVariableDirty(ownerWindow, newValue, colorVariable.Value);
            
            colorVariable.SetValue(newValue);
        }
        else if (variable.GetValueType().IsSubclassOf(typeof(Enum)))
        {
            var method = variable.GetType().GetMethod("GetValue", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var value = method.Invoke(variable, null);
            
            Enum newValue = EditorGUILayout.EnumPopup(name, value as Enum,GUILayout.MinWidth(100));
            CheckVariableDirty(ownerWindow, newValue, value as Enum);
            
            variable.SetValue(newValue);
        }else if (variable is BBoardVarUnityTransform transformVariable)
        {
            var transform = EditorGUILayout.ObjectField(name,transformVariable.Value, typeof(Transform),GUILayout.MinWidth(100));
            transformVariable.SetValue(transform);
        }
        else
        {
            EditorGUILayout.LabelField($"Unsupported type: {variable.GetType()}");
        }
    }

    private static void CheckVariableDirty<T>(BehaviourTreeEditor ownerWindow, T newValue, T oldValue)
    {
        bool dirty = false;
        if (newValue != null && oldValue != null)
        {
            dirty = !newValue.Equals(oldValue);
        }
        else
        {
            dirty = newValue != null || oldValue != null;
        }
        
        if (dirty)
        {
            ownerWindow?.RecordUndo($"Variable {typeof(T)} changed");
        }
    }
}
