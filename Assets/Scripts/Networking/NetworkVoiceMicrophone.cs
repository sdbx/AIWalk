using UnityEngine;

// PlayerVoiceChat remains as a compatibility name for existing scenes.
[RequireComponent(typeof(NetworkIdentity))]
[RequireComponent(typeof(NetworkVoiceSpeaker))]
public sealed class NetworkVoiceMicrophone : PlayerVoiceChat
{
    protected override void Awake()
    {
        if (!TryGetComponent<NetworkVoiceSpeaker>(out _))
            gameObject.AddComponent<NetworkVoiceSpeaker>();
        base.Awake();
    }
}
