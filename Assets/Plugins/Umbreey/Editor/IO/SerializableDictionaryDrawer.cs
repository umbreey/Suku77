using UnityEditor;
using UnityEngine;
using UnityEditorInternal;
using Umbreey;

[CustomPropertyDrawer(typeof(SerializableDictionary<,>))]
public class SerializableDictionaryDrawer : PropertyDrawer
{
    private ReorderableList _reorderableList;
    private SerializedProperty _keysProperty;
    private SerializedProperty _valuesProperty;

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        InitList(property);
        return _reorderableList.GetHeight() + EditorGUIUtility.singleLineHeight;
    }

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        InitList(property);
        
        // 绘制标题
        Rect headerRect = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
        EditorGUI.LabelField(headerRect, label, EditorStyles.boldLabel);
        
        // 绘制列表
        Rect listRect = new Rect(position.x, position.y + EditorGUIUtility.singleLineHeight, 
                                 position.width, position.height - EditorGUIUtility.singleLineHeight);
        _reorderableList.DoList(listRect);
    }

    private void InitList(SerializedProperty property)
    {
        if (_reorderableList != null) return;

        _keysProperty = property.FindPropertyRelative("keys");
        _valuesProperty = property.FindPropertyRelative("values");

        _reorderableList = new ReorderableList(
            property.serializedObject,
            _keysProperty,
            draggable: true,
            displayHeader: false,
            displayAddButton: true,
            displayRemoveButton: true
        );

        _reorderableList.drawElementCallback = (rect, index, isActive, isFocused) =>
        {
            rect.height = EditorGUIUtility.singleLineHeight;
            rect.y += 2;

            // 计算两个字段的宽度
            float halfWidth = rect.width / 2 - 5;
            
            // Key字段
            Rect keyRect = new Rect(rect.x, rect.y, halfWidth, rect.height);
            SerializedProperty keyProp = _keysProperty.GetArrayElementAtIndex(index);
            EditorGUI.PropertyField(keyRect, keyProp, GUIContent.none);

            // Value字段
            Rect valueRect = new Rect(rect.x + halfWidth + 10, rect.y, halfWidth, rect.height);
            SerializedProperty valueProp = _valuesProperty.GetArrayElementAtIndex(index);
            EditorGUI.PropertyField(valueRect, valueProp, GUIContent.none);
        };

        // 添加新元素时同步增加value
        _reorderableList.onAddCallback = (list) =>
        {
            _keysProperty.arraySize++;
            _valuesProperty.arraySize++;
            
            // 设置默认值（可选）
            var newKeyProp = _keysProperty.GetArrayElementAtIndex(_keysProperty.arraySize - 1);
            var newValueProp = _valuesProperty.GetArrayElementAtIndex(_valuesProperty.arraySize - 1);
            
            // 根据类型设置默认值
            SetDefaultValue(newKeyProp);
            SetDefaultValue(newValueProp);
        };

        // 删除时同步删除value
        _reorderableList.onRemoveCallback = (list) =>
        {
            int index = list.index;
            if (index >= 0 && index < _keysProperty.arraySize)
            {
                _keysProperty.DeleteArrayElementAtIndex(index);
                _valuesProperty.DeleteArrayElementAtIndex(index);
            }
        };
    }

    private void SetDefaultValue(SerializedProperty prop)
    {
        switch (prop.propertyType)
        {
            case SerializedPropertyType.Integer:
                prop.intValue = 0;
                break;
            case SerializedPropertyType.Float:
                prop.floatValue = 0f;
                break;
            case SerializedPropertyType.String:
                prop.stringValue = "";
                break;
            // 可以继续添加其他类型...
        }
    }
}