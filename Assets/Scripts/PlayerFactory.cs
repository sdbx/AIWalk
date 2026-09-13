using UnityEngine;

public class PlayerFactory : MonoBehaviour, INetworkObjectFactory
{
    [SerializeField]
    private GameObject playerPrefab;

    public GameObject Create(NetworkObjectData data)
    {
        Debug.Log("CREATE PLAYER");
        var newPlayer = Instantiate(playerPrefab);
        newPlayer.GetComponent<NetworkIdentity>().SetId(data.networkId);
        newPlayer.transform.position = data.position;
        newPlayer.transform.rotation = data.rotation;

        return newPlayer;
    }
}
