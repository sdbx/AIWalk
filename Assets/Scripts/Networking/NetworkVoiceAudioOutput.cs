using System;
using UnityEngine;

[AddComponentMenu("")]
public sealed class NetworkVoiceAudioOutput : MonoBehaviour
{
    private Action<float[], int, int> render;
    private int outputSampleRate;

    public void Configure(Action<float[], int, int> renderCallback, int sampleRate)
    {
        render = renderCallback;
        outputSampleRate = sampleRate;
    }

    private void OnAudioFilterRead(float[] output, int channels)
    {
        Array.Clear(output, 0, output.Length);
        render?.Invoke(output, channels, outputSampleRate);
    }
}
