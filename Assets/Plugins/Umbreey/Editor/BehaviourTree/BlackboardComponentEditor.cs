using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Umbreey;

[CustomEditor(typeof(BlackboardComponent))]
public class BlackboardComponentEditor : Editor
{
    public override void OnInspectorGUI()
    {
        base.OnInspectorGUI();
        BlackboardComponent scriptObject = (BlackboardComponent)target;
        var blackboard = scriptObject.Blackboard;
        BlackboardEditorHelper.DrawInspector(blackboard,scriptObject.gameObject);
    }  
}