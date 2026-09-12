using UnityEngine;
using UnityEngine.Events;

public class Interactable : MonoBehaviour
{
  public UnityEvent OnInteract;
  public UnityEvent OnCursorEnter;
  public UnityEvent OnCursorExit;

  [SerializeField]
  private GameObject outlineObject;

  public void Interact()
  {
    OnInteract.Invoke();
  }

  LayerMask startLayer;
  void Start()
  {
    if (outlineObject == null) outlineObject = gameObject;
    startLayer = outlineObject.layer;
  }

  public void EnterCursor()
  {
    OnCursorEnter.Invoke();
    outlineObject.layer = LayerMask.NameToLayer("Outline");
  }
  public void ExitCursor()
  {
    OnCursorExit.Invoke();
    outlineObject.layer = startLayer;
  }
}