using System;
using System.Threading;
using AIWalk.Networking;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(NetworkIdentity))]
public class SyncRigidbody : MonoBehaviour
{
    private Rigidbody rigidbody;

    [Header("Network")]
    [SerializeField]
    private NetworkIdentity networkIdentity;
    [SerializeField]
    private NetworkClient networkClient;

    

    private enum SyncMode
    {
        SendOnly,
        ReceiveOnly,
        HostCalculation,
        SendAndReceive,
    }

    [Header("Sync Settings")]
    [SerializeField]
    private SyncMode syncMode = SyncMode.HostCalculation;
    [SerializeField]
    private float syncInterval = 0.1f;
    private float timer = 0f;

    private RigidbodyState lastReceivedState = new RigidbodyState();
    private RigidbodyState lastSentState = new RigidbodyState();


    void Awake()
    {
        rigidbody = GetComponent<Rigidbody>();
        if(!networkIdentity)
        {
            networkIdentity = GetComponent<NetworkIdentity>();
        }

    }

    void Start()
    {
        NetworkClient.GetInstance(ref networkClient);
        networkClient.SubcribePhysics(networkIdentity.Id, ReceiveUpdate);
        if(!networkClient.IsConnected){
            networkClient.onServerConnected.AddListener(SetKinetic);
        }
        else
        {
            SetKinetic();
        }
    }

    private void SetKinetic()
    {
        switch (syncMode)
        {
            case SyncMode.SendOnly:
                {
                    rigidbody.isKinematic = false;
                    break;
                }
            case SyncMode.ReceiveOnly:
                {
                    rigidbody.isKinematic = true;
                            Debug.Log("리시브 키네틱 설정");
                    break;
                }
            case SyncMode.HostCalculation:
                {
                    rigidbody.isKinematic = !networkClient.IsHost;
                    break;
                }
            case SyncMode.SendAndReceive:
                {
                    rigidbody.isKinematic = false;
                    break;
                }
        }
    }

    void Update()
    {

    }

    void FixedUpdate()
    {
        if(!networkClient.IsConnected)
        {
            return;
        }
        if(syncMode == SyncMode.SendOnly || (networkClient.IsHost && syncMode != SyncMode.ReceiveOnly) )
        {
            timer+=Time.fixedDeltaTime;
            if (timer > syncInterval)
            {
                SendUpdate();
                timer = 0;
            }
        }
        if(syncMode == SyncMode.ReceiveOnly ||(!networkClient.IsHost && syncMode != SyncMode.SendOnly))
        {
            if (lastReceivedState.updated)
            {
                return;
            }
            rigidbody.MovePosition(lastReceivedState.position);
            rigidbody.MoveRotation(lastReceivedState.rotation);
            rigidbody.linearVelocity = lastReceivedState.velocity;
            rigidbody.angularVelocity = lastReceivedState.angularVelocity;

            lastReceivedState.updated = true;
        }

    }

    private void SendUpdate()
    {
        lastSentState.FromRigidBody(rigidbody);
        networkClient.SendPhyscis(networkIdentity.Id, lastSentState.ToBytes());
    }

    private void ReceiveUpdate(byte[] payload)
    {
       lastReceivedState.FromBytes(payload);
       lastReceivedState.updated = false;
    }

}

internal class RigidbodyState
{
    public bool updated = true;
    public Vector3 position { get; set; }
    public Quaternion rotation { get; set; }
    public Vector3 velocity { get; set; }
    public Vector3 angularVelocity { get; set; }


    public byte[] ToBytes()
    {
        var src = new float[]
        {
                position.x,position.y,position.z,
                rotation.x,rotation.y,rotation.z,rotation.w,
                velocity.x,velocity.y,velocity.z,
                angularVelocity.x,angularVelocity.y,angularVelocity.z
        };
        return ByteUtils.FloatsToBytes(src);
    }

    public void FromBytes(byte[] bytes)
    {
        var values = ByteUtils.BytesToFloats(bytes);
        position = new Vector3(values[0], values[1], values[2]);
        rotation = new Quaternion(values[3], values[4], values[5], values[6]);
        velocity = new Vector3(values[7], values[8], values[9]);
        angularVelocity = new Vector3(values[10], values[11], values[12]);
    }

    public void FromRigidBody(Rigidbody rigidbody)
    {
        position = rigidbody.position;
        rotation = rigidbody.rotation;
        velocity = rigidbody.linearVelocity;
        angularVelocity = rigidbody.angularVelocity;
    }
}