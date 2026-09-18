using System;
using System.Collections.Generic;
using AIWalk.Networking;
using UnityEngine;

[RequireComponent(typeof(NetworkIdentity))]
public abstract class NetBehaviour : MonoBehaviour
{
  NetworkClient networkClient;
  NetworkIdentity networkIdentity;
  protected bool IsHost
  {
    get
    {
      if (networkClient == null) return false;
      return networkClient.IsHost;
    }
  }

  protected string NetworkId { get => networkIdentity.Id; }

  /// <summary>
  /// Use OnStart instead of Start.
  /// </summary>
  protected void Start()
  {
    NetworkClient.GetInstance(ref networkClient);

    if (networkClient.IsConnected) _onNetworkReady();
    else networkClient.onServerConnected.AddListener(_onNetworkReady);

    networkIdentity = GetComponent<NetworkIdentity>();

    OnStart();
  }

  /// <summary>
  /// Use OnEnable2 instead of OnEnable.
  /// </summary>
  protected void OnEnable()
  {
    SubscribeEvents();
    OnEnable2();
  }

  /// <summary>
  /// Use OnDisable2 instead of OnDisable.
  /// </summary>
  protected void OnDisable()
  {
    UnsubscribeEvents();
    OnDisable2();
  }

  void _onNetworkReady()
  {
    networkClient.onServerConnected.RemoveListener(_onNetworkReady);

    OnNetworkReady();
  }
  /// <summary>
  /// Override this method rather than Start.
  /// </summary>
  protected virtual void OnStart() { }

  /// <summary>
  /// Override this method rather than OnEnable.
  /// </summary>
  protected virtual void OnEnable2() { }

  /// <summary>
  /// Override this method rather than OnDisable.
  /// </summary>
  protected virtual void OnDisable2() { }

  /// <summary>
  /// Called after the NetworkClient is connected.
  /// </summary>
  protected virtual void OnNetworkReady() { }

  protected void SendEvent<T>(string topic, EventAudience audience, T data)
  {
    if (!enabled) return;
    networkClient.SendEvent($"{topic}/{networkIdentity.Id}", audience, data);
  }
  protected void SendEvent<T>(string topic, string peerId, T data)
  {
    if (!enabled) return;
    networkClient.SendEventTo($"{topic}/{networkIdentity.Id}", peerId, data);
  }

  private readonly List<EventObject> eventObjects = new();

  void SubscribeEvents()
  {
    foreach (var subscribedEvent in eventObjects)
    {
      subscribedEvent.Subscribe();
    }
  }

  void UnsubscribeEvents()
  {
    foreach (var subscribedEvent in eventObjects)
    {
      subscribedEvent.Unsubscribe();
    }
  }

  /// <summary>
  /// Works the same way as a regular Subscribe.
  /// Automatically unsubscribed when disabled.
  /// For functions that automatically attach a NetworkIdentity, see Subscribe.
  /// </summary>
  /// <param name="topic">Event name</param>
  /// <param name="callback">Event callback</param>
  protected void SubscribeGlobalEvent(string topic, Action<GameBackendEvent> callback)
  {
    EventObject item = new(networkClient, topic, callback);
    if (enabled)
      item.Subscribe();
    eventObjects.Add(item);
  }

  /// <summary>
  /// Subscribes to the topic with the NetworkIdentity automatically appended to it.
  /// Automatically unsubscribed when disabled.
  /// For regular Subscribe, see SubscribeGlobalEvent.
  /// </summary>
  /// <param name="topic">Event name</param>
  /// <param name="callback">Event callback</param>
  protected void Subscribe(string topic, Action<GameBackendEvent> callback)
  {
    SubscribeGlobalEvent($"{topic}/{networkIdentity.Id}", callback);
  }

  class EventObject
  {
    readonly NetworkClient networkClient;
    readonly string topic;
    readonly Action<GameBackendEvent> callback;
    bool subscribed = false;

    public EventObject(NetworkClient networkClient, string topic, Action<GameBackendEvent> callback)
    {
      this.networkClient = networkClient;
      this.topic = topic;
      this.callback = callback;
    }

    public void Subscribe()
    {
      if (subscribed) return;
      networkClient.Subscribe(topic, callback);
      subscribed = true;
    }

    public void Unsubscribe()
    {
      if (!subscribed) return;
      networkClient.Unsubscribe(topic, callback);
      subscribed = false;
    }
  }
}