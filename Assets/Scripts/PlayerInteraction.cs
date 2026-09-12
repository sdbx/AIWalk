using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerInteraction : MonoBehaviour
{
  [SerializeField]
  private float interactDistance = 3.0f;

  [SerializeField]
  private Camera camera;

  public void OnInteracted(InputAction.CallbackContext context)
  {
    if (!context.started) return;
    Ray ray = new(camera.transform.position, camera.transform.forward);
    if (!Physics.Raycast(ray, out RaycastHit hit, interactDistance)) return;
    if (!hit.collider.TryGetComponent<Interactable>(out var interactable)) return;

    interactable.Interact();
  }

  Interactable prevInteractable;

  void Update()
  {
    Ray ray = new(camera.transform.position, camera.transform.forward);
    if (Physics.Raycast(ray, out RaycastHit hit, interactDistance)
     && hit.collider.TryGetComponent<Interactable>(out var interactable))
    {
      if (prevInteractable == null)
      {
        interactable.EnterCursor();
      }
      prevInteractable = interactable;
    } else
    {
      if (prevInteractable != null)
      {
        prevInteractable.ExitCursor();
      }
      prevInteractable = null;
    }

  }
}
