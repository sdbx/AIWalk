using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

[RequireComponent(typeof(NetworkIdentity))]
public sealed class NetworkVoiceSpeaker : MonoBehaviour
{
    public static event Action<NetworkVoiceSpeaker> SpeakerEnabled;
    public static event Action<NetworkVoiceSpeaker> SpeakerDisabled;

    private static readonly object ActiveLock = new();
    private static readonly List<NetworkVoiceSpeaker> Active = new();

    [SerializeField] private NetworkClient networkClient;
    [SerializeField] private NetworkIdentity networkIdentity;
    [SerializeField] private Transform soundOrigin;
    [SerializeField, Range(0f, 2f)] private float playbackVolume = 1f;
    [SerializeField, Min(0f)] private float minimumDistance = 1f;
    [SerializeField, Min(0.01f)] private float maximumDistance = 20f;
    [SerializeField, Range(0.1f, 4f)] private float falloffExponent = 1.5f;

    private readonly object listenerLock = new();
    private IngameMicrophone[] listenerSnapshot = Array.Empty<IngameMicrophone>();
    private string currentNetworkId;
    private bool subscribed;
    private volatile bool shuttingDown;

    public string NetworkId => Volatile.Read(ref currentNetworkId);
    public Vector3 SoundPosition => soundOrigin != null ? soundOrigin.position : transform.position;

    public static NetworkVoiceSpeaker[] GetActiveSpeakers()
    {
        lock (ActiveLock)
            return Active.ToArray();
    }

    private void Awake()
    {
        if (networkIdentity == null)
            networkIdentity = GetComponent<NetworkIdentity>();
    }

    private void OnEnable()
    {
        shuttingDown = false;
        lock (ActiveLock)
        {
            if (!Active.Contains(this))
                Active.Add(this);
        }
        SpeakerEnabled?.Invoke(this);
        StartCoroutine(SubscribeWhenReady());
    }

    private IEnumerator SubscribeWhenReady()
    {
        while (networkClient == null)
        {
            networkClient = NetworkClient.Instance;
            if (networkClient == null)
                yield return null;
        }
        yield return new WaitUntil(() => networkClient != null && networkClient.IsConnected);
        if (!isActiveAndEnabled)
            yield break;

        networkClient.SubscribeVoice(OnVoicePcmReceived);
        networkClient.SubscribeLocalVoice(OnLocalVoicePcmSent);
        subscribed = true;
    }

    private void Update()
    {
        if (networkIdentity != null && networkIdentity.HasId)
            Volatile.Write(ref currentNetworkId, networkIdentity.Id);
    }

    public void AddListener(IngameMicrophone listener)
    {
        if (listener == null)
            return;

        lock (listenerLock)
        {
            var listeners = new List<IngameMicrophone>(listenerSnapshot);
            if (listeners.Contains(listener))
                return;
            listeners.Add(listener);
            listenerSnapshot = listeners.ToArray();
        }
    }

    public void RemoveListener(IngameMicrophone listener)
    {
        lock (listenerLock)
        {
            var listeners = new List<IngameMicrophone>(listenerSnapshot);
            if (!listeners.Remove(listener))
                return;
            listenerSnapshot = listeners.ToArray();
        }
    }

    public float GetVolumeAt(Vector3 listenerPosition)
    {
        return playbackVolume * CalculateAttenuation(listenerPosition);
    }

    public float CalculateAttenuation(Vector3 listenerPosition)
    {
        return VoiceDistanceAttenuation.Calculate(
            SoundPosition,
            listenerPosition,
            minimumDistance,
            maximumDistance,
            falloffExponent);
    }

    private void OnVoicePcmReceived(string sourceNetworkId, byte[] pcm16)
    {
        ForwardPcm(sourceNetworkId, pcm16, false);
    }

    private void OnLocalVoicePcmSent(string sourceNetworkId, byte[] pcm16)
    {
        ForwardPcm(sourceNetworkId, pcm16, true);
    }

    private void ForwardPcm(string sourceNetworkId, byte[] pcm16, bool isLocalMicrophone)
    {
        if (shuttingDown || !string.Equals(sourceNetworkId, NetworkId, StringComparison.Ordinal))
            return;
        if (pcm16 == null || pcm16.Length == 0 || pcm16.Length % 2 != 0)
            return;

        IngameMicrophone[] listeners = listenerSnapshot;
        for (int i = 0; i < listeners.Length; i++)
            listeners[i]?.ReceivePcm(this, pcm16, isLocalMicrophone);
    }

    private void OnDisable()
    {
        shuttingDown = true;
        if (subscribed)
        {
            networkClient?.UnsubscribeVoice(OnVoicePcmReceived);
            networkClient?.UnsubscribeLocalVoice(OnLocalVoicePcmSent);
            subscribed = false;
        }
        lock (ActiveLock)
            Active.Remove(this);
        SpeakerDisabled?.Invoke(this);
    }
}
