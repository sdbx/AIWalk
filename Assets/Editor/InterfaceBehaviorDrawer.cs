using System;
using UnityEditor;
using UnityEngine;

[CustomPropertyDrawer(typeof(InterfaceBehavior<>), true)]
public class InterfaceBehaviorDrawer : PropertyDrawer
{
    public override void OnGUI(
        Rect position,
        SerializedProperty property,
        GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);

        var valueProperty = property.FindPropertyRelative("value");

        if (valueProperty == null)
        {
            EditorGUI.LabelField(position, label.text, "value 필드를 찾을 수 없음");
            EditorGUI.EndProperty();
            return;
        }

        Type interfaceType = GetGenericInterfaceType();

        if (interfaceType == null)
        {
            EditorGUI.LabelField(position, label.text, "Generic 타입을 찾을 수 없음");
            EditorGUI.EndProperty();
            return;
        }

        UnityEngine.Object current = valueProperty.objectReferenceValue;

        UnityEngine.Object selected = EditorGUI.ObjectField(
            position,
            label,
            current,
            typeof(MonoBehaviour),
            true
        );

        if (selected == null)
        {
            valueProperty.objectReferenceValue = null;
        }
        else if (selected is MonoBehaviour behaviour)
        {
            if (interfaceType.IsAssignableFrom(behaviour.GetType()))
            {
                valueProperty.objectReferenceValue = behaviour;
            }
            else
            {
                Debug.LogWarning(
                    $"{behaviour.GetType().Name}은(는) " +
                    $"{interfaceType.Name}을 구현하지 않습니다."
                );
            }
        }

        EditorGUI.EndProperty();
    }

    private Type GetGenericInterfaceType()
    {
        Type fieldType = fieldInfo.FieldType;

        if (!fieldType.IsGenericType)
            return null;

        return fieldType.GetGenericArguments()[0];
    }
}