using AIWalk.Networking;
using UnityEngine;
using UnityEngine.Events;

public class NetworkClient : MonoBehaviour
{
    public static NetworkClient Instance { get; private set; }

    private GameBackendWebSocketClient socketClient;
    private GameBackendEventClient eventClient;

    [SerializeField] private string backendUrl = "ws://localhost:8080";
    [SerializeField] private int maxMessageBytes = 256 * 1024;
    private bool initialized = false;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        socketClient = new GameBackendWebSocketClient(256 * 1024);
        eventClient = new GameBackendEventClient(socketClient);
    }

    public void SendEvent(string eventName, EventAudience audience, string data)
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

    public void SubscribeUnityEvent(string eventName, UnityEvent unityEvent)
    {
        eventClient.Subscribe(eventName, (e)=>{unityEvent.Invoke();});
    }

    public void Subscribe(string eventName, System.Action<GameBackendEvent> callback)
    {
        eventClient.Subscribe(eventName, callback);
    }

    private void Start()
    {
        StartCoroutine(ConnectToServer());
    }

    private System.Collections.IEnumerator ConnectToServer()
    {
        yield return socketClient.ConnectAsync(GameBackendWebSocketClient.BuildServerUri(backendUrl));
    }

    private void OnConnected()
    {
        Debug.Log("Connected to server.");

        eventClient.Subscribe("example_event", (data) =>
        {
            Debug.Log($"Received event: {data}");
        });
    }

    private void Update()
    {
        if(socketClient.IsConnected)
        {
            if(!initialized)
            {
                initialized = true;
                OnConnected();
            }
            UpdateServers();
        }
    }

    private void UpdateServers()    
    {
        socketClient.Pump();
    }
}
