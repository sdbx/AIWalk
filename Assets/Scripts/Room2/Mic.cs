using System;
using Unity.VisualScripting;
using UnityEngine;

public class Mic : NetBehaviour
{
    [SerializeField]
    private IngameMicrophone microphone;

    [SerializeField]
    private float maxRecordTime = 30;

    private float timer = 0;

    [SerializeField]
    private SimpleButton button;


    protected override void OnNetworkReady()
    {
        button.OnPressed.AddListener(StartRecord);
        button.OnReleased.AddListener(StopRecord);
    }

    
    void Update()
    {
        if (button.pressed && timer > 0)
        {
            timer-=Time.deltaTime;
        }
        else if(button.pressed)
        {
            button.Release();
        }
    }

    public void StartRecord()
    {
        microphone.StartRecording();
        timer = maxRecordTime;
        Debug.Log("[MicTest]Recording");
    }

    public void StopRecord()
    {
        Debug.Log("[MicTest]Records stoped");
        RecordedVoiceAudio audio = microphone.StopRecording();
        Debug.Log(audio.Pcm16);
        Debug.Log("[MicTest]Sending");
        SendEvent("audio.test",AIWalk.Networking.EventAudience.Api,audio.ToServerData());
    }
}
