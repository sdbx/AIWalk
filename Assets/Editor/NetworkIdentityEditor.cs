using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

[CustomEditor(typeof(NetworkIdentity))]
public class NetworkIdentityEditor : Editor
{
  void OnEnable()
  {
    serializedObject.Update();
    var prop = serializedObject.FindProperty("id");

    if (string.IsNullOrEmpty(prop.stringValue))
    {
      prop.stringValue = NetworkIdentity.GenerateId();

      serializedObject.ApplyModifiedProperties();
    }
  }

  public override VisualElement CreateInspectorGUI()
  {
    var root = new VisualElement();

    InspectorElement.FillDefaultInspector(root, serializedObject, this);

    root.Add(new Button(() =>
    {
      var prop = serializedObject.FindProperty("id");

      prop.stringValue = NetworkIdentity.GenerateId();

      serializedObject.ApplyModifiedProperties();
    })
    {
      text = "ID 생성",
    });

    return root;
  }
}