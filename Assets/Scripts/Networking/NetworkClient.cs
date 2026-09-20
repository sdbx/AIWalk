using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AIWalk.Networking;
using UnityEngine;
using UnityEngine.Events;

[Serializable]
public class ConnectionReadyPayload
{
    public string peerId;
    public int peerSlot;
    public string role;
}

public class NetworkClient : MonoBehaviour
{
    public static NetworkClient Instance { get; private set; }

    private GameBackendWebSocketClient socketClient;
    private GameBackendEventClient eventClient;
    private readonly object voiceSendLock = new();
    private readonly Queue<QueuedVoicePacket> voiceSendQueue = new();
    private uint voiceSequence;
    private bool voiceSendLoopRunning;
    private Exception voiceSendError;
    private event Action<string, byte[]> LocalVoicePcmSent;
    [field:SerializeField]
    public UnityEvent onServerConnected{get;private set;} = new UnityEvent();

    [SerializeField] private string backendUrl = "ws://localhost:8080";
    [SerializeField] private int maxMessageBytes = 2 * 1024 * 1024;

    public bool IsHost => role == "host";

    public bool IsConnected => socketClient != null && socketClient.IsConnected ;

    public string peerId { get; private set; }
    public int peerSlot { get; private set; }
    public string role { get; private set; }


    public static void GetInstance(ref NetworkClient custom)
    {
        if (custom == null)
        {
            if (Instance == null)
            {
                throw new Exception("NetworkClient instance is not set. Please ensure that a NetworkClient component is present in the scene.");
            }
            custom = Instance;
        }
    }

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        socketClient = new GameBackendWebSocketClient(maxMessageBytes);
        eventClient = new GameBackendEventClient(socketClient);
        InitSubcriptions();
    }

    private void InitSubcriptions()
    {   
        eventClient.Subscribe("connection.ready", OnServerReady);

        eventClient.Subscribe("host.promoted", data => {
            role = "host";
        });
    }

    private void OnServerReady(GameBackendEvent data)
    {
        var payload = data.DeserializePayload<ConnectionReadyPayload>();
        peerId = payload.peerId;
        peerSlot = payload.peerSlot;
        role = payload.role;
        Debug.Log("I am " + role);
        Debug.Log("Connected to server Ready.");
        onServerConnected.Invoke();
    }

    public void SendEvent<T>(string eventName, EventAudience audience, T data)
    {
        if (socketClient.IsConnected)
        {
            eventClient.PublishAsync(eventName, audience, data);
        }
        else
        {
            Debug.LogWarning("Cannot send event. Not connected to server.");
        }
    }

    public void SendEventTo<T>(string eventName,string peerId,T data)
    {
        if (socketClient.IsConnected)
        {
            eventClient.SendToPeerAsync(eventName, peerId, data);
        }
        else
        {
            Debug.LogWarning("Cannot send event. Not connected to server.");
        }
    }

    public void SendPhyscis(string id,byte[] payload)
    {
        socketClient.SendPhysicsSnapshotAsync(id,payload,0);
    }

    public void SubcribePhysics(string id,Action<byte[]> callback)
    {
        socketClient.SubscribePhysicsSnapshot(id,callback);
    }

    public void SendVoicePcm(string networkId, byte[] pcm16)
    {
        if (!socketClient.IsConnected)
        {
            Debug.LogWarning("Cannot send voice. Not connected to server.");
            return;
        }

        LocalVoicePcmSent?.Invoke(networkId, pcm16);

        lock (voiceSendLock)
        {
            voiceSendQueue.Enqueue(new QueuedVoicePacket(networkId, pcm16));

            if (voiceSendLoopRunning)
                return;
            voiceSendLoopRunning = true;
        }

        _ = SendQueuedVoiceAsync();
    }

    private async Task SendQueuedVoiceAsync()
    {
        while (true)
        {
            QueuedVoicePacket packet;
            lock (voiceSendLock)
            {
                if (voiceSendQueue.Count == 0)
                {
                    voiceSendLoopRunning = false;
                    return;
                }
                packet = voiceSendQueue.Dequeue();
            }

            try
            {
                await socketClient.SendVoicePcmAsync(packet.NetworkId, packet.Pcm16, voiceSequence++);
            }
            catch (Exception exception)
            {
                lock (voiceSendLock)
                {
                    voiceSendQueue.Clear();
                    voiceSendError = exception;
                }
            }
        }
    }

    public void SubscribeVoice(Action<string, byte[]> callback)
    {
        socketClient.RealtimeVoicePcmReceived += callback;
    }

    public void UnsubscribeVoice(Action<string, byte[]> callback)
    {
        socketClient.RealtimeVoicePcmReceived -= callback;
    }

    public void SubscribeLocalVoice(Action<string, byte[]> callback)
    {
        LocalVoicePcmSent += callback;
    }

    public void UnsubscribeLocalVoice(Action<string, byte[]> callback)
    {
        LocalVoicePcmSent -= callback;
    }

    private readonly struct QueuedVoicePacket
    {
        public readonly string NetworkId;
        public readonly byte[] Pcm16;

        public QueuedVoicePacket(string networkId, byte[] pcm16)
        {
            NetworkId = networkId;
            Pcm16 = pcm16;
        }
    }
    public void SubscribeUnityEvent(string eventName, UnityEvent unityEvent)
    {
        eventClient.Subscribe(eventName, (e)=>{unityEvent.Invoke();});
    }

    public void Subscribe(string eventName, System.Action<GameBackendEvent> callback)
    {
        eventClient.Subscribe(eventName, callback);
    }

    public void Unsubscribe(string eventName, System.Action<GameBackendEvent> callback)
    {
        eventClient.Unsubscribe(eventName, callback);
    }

    private void Start()
    {
        StartCoroutine(ConnectToServer());
    }

    private System.Collections.IEnumerator ConnectToServer()
    {
        var task = socketClient.ConnectAsync(GameBackendWebSocketClient.BuildServerUri(backendUrl));
        yield return new WaitUntil(() => task.IsCompleted);
        if (socketClient.IsConnected)
        {
            Debug.Log("Successfully connected to server.");
        }
        else
        {
            Debug.LogError($"Failed to connect to server: {task.Exception}");
        }
    }


    private void Update()
    {
        Exception sendError = null;
        lock (voiceSendLock)
        {
            if (voiceSendError != null)
            {
                sendError = voiceSendError;
                voiceSendError = null;
            }
        }
        if (sendError != null)
            Debug.LogException(sendError);

        if(socketClient.IsConnected)
        {
            UpdateServers();
        }
    }

    private void UpdateServers()    
    {
        socketClient.Pump();
    }

    private void OnDestroy()
    {
        LocalVoicePcmSent = null;
        eventClient?.Dispose();
        socketClient?.Dispose();

        if (ReferenceEquals(Instance, this))
            Instance = null;
    }
}
