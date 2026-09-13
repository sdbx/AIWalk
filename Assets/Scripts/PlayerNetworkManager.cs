using System;
using UnityEngine;


[Serializable]
public class PeerJoinedPayload
{
    public string peerId;
    public int peerSlot;
    public string role;
}

public class PlayerNetworkManager : MonoBehaviour
{
    [SerializeField]
    private NetworkClient networkClient;
    [SerializeField]
    private NetworkObjectManager networkObjectManager;
    [SerializeField]
    private NetworkIdentity networkIdentity;

    void Start()
    {
        NetworkClient.GetInstance(ref networkClient);
        networkClient.onServerConnected.AddListener(() =>
        {
            Debug.Log("try connected creation send");
            SendCreation();
            Debug.Log("connected creation send");
        });
        networkClient.Subscribe("peer.joined", data =>
        {
            var joined = data.DeserializePayload<PeerJoinedPayload>();
            Debug.Log(joined.peerId+" joined creation send");
            SendCreation(joined.peerId);
        });

    }

    public void SendCreation(string peerId = null)
    {
        var data = NetworkObjectData.FromGameObject("player", gameObject);
        if (peerId==null)
        {
            networkObjectManager.sendCreateObjectEvent(data);
            return;
        }
        networkObjectManager.sendCreateObjectEventTo(data,peerId);
    }
    
    void Update()
    {
        
    }
}
