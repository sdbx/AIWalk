using AIWalk.Networking;
using UnityEngine;
using UnityEngine.Events;

public class Interactable : MonoBehaviour
{
  [Header("Events")]
  public UnityEvent OnInteract;
  public UnityEvent OnCursorEnter;
  public UnityEvent OnCursorExit;

  [Header("Network")]
  public NetworkIdentity networkIdentity;


  NetworkClient networkClient;
  string interactEventName;
  bool isSubscribed = false;

  void Start()
  {
    networkClient = NetworkClient.Instance;
    InitializeNetwork();
    SubscribeNetworkEvent();
  }
  void OnEnable()
  {
    SubscribeNetworkEvent();
  }
  void OnDisable()
  {
    UnsubscribeNetworkEvent();
    SetLayerRecursively(gameObject, "Default");
  }

  public void Interact()
  {
    if (!enabled) return;
    OnInteract.Invoke();
    SendNetworkInteractEvent();
  }

  public void EnterCursor()
  {
    if (!enabled) return;
    OnCursorEnter.Invoke();
    SetLayerRecursively(gameObject, "Outline");
  }
  public void ExitCursor()
  {
    if (!enabled) return;
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

  void InitializeNetwork()
  {
    if (networkIdentity == null) return;
    if (networkClient == null)
    {
      Debug.LogError($"[{nameof(Interactable)}] NetworkClient is not available.", this);
      return;
    }
    if (string.IsNullOrEmpty(networkIdentity.Id))
    {
      Debug.LogError($"[{nameof(Interactable)}] NetworkIdentity.Id is empty.", this);
      return;
    }

    interactEventName = networkIdentity.Id + "/interact";
  }

  void SubscribeNetworkEvent()
  {
    if (isSubscribed || networkClient == null || string.IsNullOrEmpty(interactEventName)) return;
    Debug.Log("[SUB] " + interactEventName, this);
    networkClient.Subscribe(interactEventName, OnNetworkInteract);
    isSubscribed = true;
  }
  void UnsubscribeNetworkEvent()
  {
    if (!isSubscribed || networkClient == null || string.IsNullOrEmpty(interactEventName)) return;
    Debug.Log("[UNSUB] " + interactEventName, this);
    networkClient.Unsubscribe(interactEventName, OnNetworkInteract);
    isSubscribed = false;
  }
  void SendNetworkInteractEvent()
  {
    if (networkClient == null || string.IsNullOrEmpty(interactEventName)) return;
    Debug.Log("[SEND] " + interactEventName, this);
    networkClient.SendEvent(interactEventName, EventAudience.Others, "");
  }

  void OnNetworkInteract(GameBackendEvent _)
  {
    Debug.Log("[RECV] " + interactEventName, this);
    OnInteract.Invoke();
  }
}