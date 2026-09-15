using UnityEngine;


[RequireComponent(typeof(NetworkIdentity))]
public class SyncState : MonoBehaviour
{
    [Header("Network")]

    [SerializeField]
    private NetworkIdentity networkIdentity;
    [SerializeField]
    private NetworkClient networkClient;

    
}
