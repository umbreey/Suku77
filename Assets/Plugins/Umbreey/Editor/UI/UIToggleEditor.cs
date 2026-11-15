using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UI;
using UnityEngine;
using UnityEngine.UI;

namespace Umbreey.Editor
{
   [CustomEditor(typeof(UIToggle), true)]
   [CanEditMultipleObjects]
   public class UIToggleEditor : ToggleEditor
   {
       SerializedProperty m_TransitionProperty;
       SerializedProperty m_OnObjectProperty;
       SerializedProperty m_OffObjectProperty;
       
       protected override void OnEnable()
       {
           base.OnEnable();
           
           m_TransitionProperty = serializedObject.FindProperty("m_Transition");
           m_OnObjectProperty = serializedObject.FindProperty("m_On");
           m_OffObjectProperty = serializedObject.FindProperty("m_Off");
       }
   
       public override void OnInspectorGUI()
       {
           base.OnInspectorGUI();
           
           var trans = (Selectable.Transition)m_TransitionProperty.enumValueIndex;
           if (trans == Selectable.Transition.None)
           {
               EditorGUILayout.Space();
               
               serializedObject.Update();
               {
                   EditorGUILayout.PropertyField(m_OnObjectProperty);
                   EditorGUILayout.PropertyField(m_OffObjectProperty);
               }
               serializedObject.ApplyModifiedProperties();            
           }
       }
   } 
    
}

