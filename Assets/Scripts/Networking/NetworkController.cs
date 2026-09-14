using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AIWalk.Networking;
using UnityEngine;
using UnityEngine.Events;

public class NetworkController : MonoBehaviour
{
  NetworkClient networkClient;

  [SerializeField]
  private NetworkIdentity identity;

  [SerializeField]
  private Dictionary<string, UnityEvent> eventData = new();

  [SerializeField]
  private Dictionary<string, List<UnityEventReference>> unityEventReferences = new();

  Dictionary<string, RegisteredEvent> registeredEvents = new();

  public void SendEvent(string eventName)
  {
    if (!enabled) return;
    var topic = EventNameToTopic(eventName);
    if (!registeredEvents.TryGetValue(topic, out var registeredEvent)) return;
    registeredEvent.Invoke(null);
    networkClient.SendEvent(topic, EventAudience.Others, "");
  }

  void OnEnable()
  {
    SubscribeEvents();
  }
  void OnDisable()
  {
    UnsubscribeEvents();
  }

  void Start()
  {
    networkClient = NetworkClient.Instance;
    SubscribeEvents();
  }

  void SubscribeEvents()
  {
    if (networkClient == null) return;
    if (identity == null || string.IsNullOrEmpty(identity.Id))
    {
      Debug.LogError("NetworkIdentity is empty.", this);
      return;
    }
    foreach (var (key, unityEvent) in eventData)
    {
      var topic = EventNameToTopic(key);
      RegisteredEvent registeredEvent = new(topic, networkClient, unityEvent);
      registeredEvents.Add(topic, registeredEvent);
    }
    foreach (var (key, references) in unityEventReferences)
    {
      foreach (var reference in references)
      {
        if (!reference.TryResolve(out var unityEvent))
        {
          Debug.LogWarning($"{reference.eventName}");
          continue;
        }
        var topic = EventNameToTopic(key);
        RegisteredEvent registeredEvent = new(topic, networkClient, unityEvent);
        registeredEvents.Add(topic, registeredEvent);
      }
    }
  }

  void UnsubscribeEvents()
  {
    if (networkClient == null) return;
    foreach (var registeredEvent in registeredEvents.Values)
    {
      registeredEvent.Dispose();
    }
  }

  string EventNameToTopic(string eventName)
  {
    return $"{identity.Id}/nc/{eventName}";
  }

  public class RegisteredEvent : IDisposable
  {
    readonly string topic;
    readonly NetworkClient networkClient;
    readonly UnityEvent unityEvent;
    public RegisteredEvent(string topic, NetworkClient networkClient, UnityEvent unityEvent)
    {
      this.topic = topic;
      this.networkClient = networkClient;
      this.unityEvent = unityEvent;

      networkClient.Subscribe(topic, Invoke);
    }

    public void Dispose()
    {
      networkClient.Unsubscribe(topic, Invoke);
    }

    public void Invoke(GameBackendEvent _)
    {
      Debug.LogWarning($"invoke: {topic}");
      unityEvent.Invoke();
    }
  }

  [Serializable]
  public class UnityEventReference
  {
    public Component component;
    public string eventName;

    public bool TryResolve(out UnityEvent unityEvent)
    {
      unityEvent = null;
      if (component == null || string.IsNullOrEmpty(eventName)) return false;

      var field = component.GetType().GetField(eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

      if (field == null) return false;

      if (!typeof(UnityEvent).IsAssignableFrom(field.FieldType)) return false;

      unityEvent = field.GetValue(component) as UnityEvent;
      return unityEvent != null;
    }
  }
}
