using System.Collections.Generic;
using AIWalk.Networking;
using Unity.Mathematics;
using UnityEngine;




public class NetworkObjectManager : MonoBehaviour
{

    [SerializeField]
    private NetworkClient networkClient;
    [SerializeField]
    private Dictionary<string,PlayerFactory> factorys = new Dictionary<string, PlayerFactory>();
    //Dictionary<string,INetworkObjectFactory> serialize안되서 임시로 PlayerFactory로 적용

    void Start()
    {
        NetworkClient.GetInstance(ref networkClient);
        networkClient.Subscribe("object.create",OnObjectCreate);
    }

    void OnObjectCreate(GameBackendEvent e)
    {
        Debug.Log("creation received");
        var data = e.DeserializePayload<NetworkObjectData>();
        factorys[data.objectType].Create(data);
    }

    public void sendCreateObjectEvent(NetworkObjectData data)
    {
        networkClient.SendEvent("object.create",EventAudience.Others,data);
    }

    public void sendCreateObjectEventTo(NetworkObjectData data,string peerId)
    {
        networkClient.SendEventTo("object.create",peerId,data);
    }


}
