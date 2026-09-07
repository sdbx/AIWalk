using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(Room))]
public class RoomEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space();

        if (GUILayout.Button("Generate Mesh"))
        {
            var room = (Room)target;

            Undo.RecordObject(
                room.GetComponent<MeshFilter>(),
                "Generate Room Mesh"
            );

            room.Generate();

            EditorUtility.SetDirty(
                room.GetComponent<MeshFilter>()
            );
        }
    }
}