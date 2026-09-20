using System;
using System.Collections.Generic;
using AIWalk.Networking;
using UnityEditor.PackageManager;
using UnityEngine;

[Serializable]
struct PlatformData
{
    public string id;
    public float height;
}


public class NewMonoBehaviourScript : NetBehaviour
{
    [SerializeField]
    private Dictionary<string,Platform> platforms = new Dictionary<string,Platform>();

   
    protected override void OnNetworkReady()
    {
        Subscribe("platfrom.move",OnPlatformEvent);
    }

    private void OnPlatformEvent(GameBackendEvent e)
    {
        PlatformData platformData = e.DeserializePayload<PlatformData>();
        if (platforms.TryGetValue(platformData.id, out var platform))
        {
            platform.moveTo(platformData.height);
        }
        else if (IsHost)
        {
            SendEvent("platform.wrong",EventAudience.Api,platformData.id);
        }
    }
}
