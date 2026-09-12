using System.Collections.Generic;
using System.Linq;
using AIWalk.Networking;
using UnityEngine;
using UnityEngine.Events;

public class TestObject : MonoBehaviour
{
    NetworkClient networkClient;

    [SerializeField] 
    private Dictionary<string, UnityEvent> eventData = new Dictionary<string, UnityEvent>();

    public void SendEvent(string eventName)
    {
        networkClient.SendEvent(eventName, EventAudience.All, "");
    }


    void Start()
    {
        if(!networkClient)
        {
            networkClient = NetworkClient.Instance;
        }

        eventData.Keys.ToList().ForEach(key =>
        {
            networkClient.SubscribeUnityEvent(key, eventData[key]);
        });
    }

    void Update()
    {
        
    }
}
