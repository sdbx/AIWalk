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
    [SerializeField]
    private float interpolationDuration = 0.1f;
    [SerializeField]
    private float teleportDistance = 3f;
    private float timer = 0f;
    private float interpolationTimer = 0f;
    private bool isInterpolating = false;

    private Vector3 interpolationStartPosition;
    private Quaternion interpolationStartRotation;
    private Vector3 interpolationStartVelocity;
    private Vector3 interpolationStartAngularVelocity;

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
            if (!isInterpolating)
            {
                return;
            }

            if (teleportDistance > 0f &&
                Vector3.Distance(rigidbody.position, lastReceivedState.position) > teleportDistance)
            {
                rigidbody.position = lastReceivedState.position;
                rigidbody.rotation = lastReceivedState.rotation;
                rigidbody.linearVelocity = lastReceivedState.velocity;
                rigidbody.angularVelocity = lastReceivedState.angularVelocity;
                isInterpolating = false;
                return;
            }

            interpolationTimer += Time.fixedDeltaTime;
            float duration = Mathf.Max(interpolationDuration, Time.fixedDeltaTime);
            float t = Mathf.Clamp01(interpolationTimer / duration);

            rigidbody.MovePosition(Vector3.Lerp(
                interpolationStartPosition,
                lastReceivedState.position,
                t));
            rigidbody.MoveRotation(Quaternion.Slerp(
                interpolationStartRotation,
                lastReceivedState.rotation,
                t));
            rigidbody.linearVelocity = Vector3.Lerp(
                interpolationStartVelocity,
                lastReceivedState.velocity,
                t);
            rigidbody.angularVelocity = Vector3.Lerp(
                interpolationStartAngularVelocity,
                lastReceivedState.angularVelocity,
                t);

            if (t >= 1f)
            {
                isInterpolating = false;
            }
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

        interpolationStartPosition = rigidbody.position;
        interpolationStartRotation = rigidbody.rotation;
        interpolationStartVelocity = rigidbody.linearVelocity;
        interpolationStartAngularVelocity = rigidbody.angularVelocity;
        interpolationTimer = 0f;
        isInterpolating = true;
    }

}

internal class RigidbodyState
{
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
