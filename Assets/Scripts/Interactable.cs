using UnityEngine;
using UnityEngine.Events;

public class Interactable : MonoBehaviour
{
  public UnityEvent OnInteract;

  public void Interact()
  {
    OnInteract.Invoke();
  }
}