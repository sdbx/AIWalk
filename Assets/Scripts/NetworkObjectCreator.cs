using System.Collections.Generic;
using AIWalk.Networking;
using Unity.Mathematics;
using UnityEngine;




public class NetworkObjectManager : MonoBehaviour
{

    [SerializeField]
    private NetworkClient networkClient;
    [SerializeField]
    private Dictionary<string,InterfaceBehavior<INetworkObjectFactory>> factorys = new Dictionary<string, InterfaceBehavior<INetworkObjectFactory>>();

    void Start()
    {
        NetworkClient.GetInstance(ref networkClient);
        networkClient.Subscribe("object.create",OnObjectCreate);
    }

    void OnObjectCreate(GameBackendEvent e)
    {
        Debug.Log("creation received");
        var data = e.DeserializePayload<NetworkObjectData>();
        factorys[data.objectType].Value.Create(data);
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
