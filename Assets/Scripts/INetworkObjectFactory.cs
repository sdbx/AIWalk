using System;
using UnityEngine;

[Serializable]
public class NetworkObjectData
{
    public string networkId;
    public Vector3 position;
    public Quaternion rotation;
    public string objectType;

    public static NetworkObjectData FromGameObject(string objectType,GameObject gameObject)
    {
        return new NetworkObjectData
        {
          networkId=gameObject.GetComponent<NetworkIdentity>().Id,
          position=gameObject.transform.position,
          rotation=gameObject.transform.rotation,
          objectType = objectType,
        };
    }
}

public interface INetworkObjectFactory
{
    GameObject Create(NetworkObjectData data);
}