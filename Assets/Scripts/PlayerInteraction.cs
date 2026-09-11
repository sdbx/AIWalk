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

    Debug.DrawLine(ray.origin, ray.origin + ray.direction * interactDistance);

    if (!Physics.Raycast(ray, out RaycastHit hit, interactDistance)) return;
    Debug.Log(hit);
    if (!hit.collider.TryGetComponent<Interactable>(out var interactable)) return;

    interactable.Interact();
  }
}
