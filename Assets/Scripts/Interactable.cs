using UnityEngine;
using UnityEngine.Events;

public class Interactable : MonoBehaviour
{
  public UnityEvent OnInteract;
  public UnityEvent OnCursorEnter;
  public UnityEvent OnCursorExit;

  public void Interact()
  {
    OnInteract.Invoke();
  }

  public void EnterCursor()
  {
    OnCursorEnter.Invoke();
    SetLayerRecursively(gameObject, "Outline");
  }
  public void ExitCursor()
  {
    OnCursorExit.Invoke();
    SetLayerRecursively(gameObject, "Default");
  }

  void SetLayerRecursively(GameObject root, string layerName)
  {
    int layer = LayerMask.NameToLayer(layerName);
    root.layer = layer;

    foreach (Transform child in root.GetComponentsInChildren<Transform>())
    {
      child.gameObject.layer = layer;
    }
  }
}