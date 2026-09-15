using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public sealed class PlayerVoiceChat : MonoBehaviour
{
    [SerializeField] private NetworkClient networkClient;
    [SerializeField] private string microphoneDevice;
    [SerializeField] private int packetMilliseconds = 20;
    [SerializeField, Range(1f, 8f)] private float microphoneGain = 3f;
    [SerializeField, Range(0f, 1f)] private float voiceOpenThreshold = 0.5f;
    [SerializeField, Range(0f, 1f)] private float voiceCloseThreshold = 0.35f;
    [SerializeField, Range(100, 500)] private int voiceHangoverMilliseconds = 250;
    [SerializeField, Range(0.05f, 0.3f)] private float targetVoiceLevel = 0.16f;
    [SerializeField, Range(1f, 6f)] private float maximumAutomaticGain = 4f;
    [SerializeField] private bool echoCancellation = true;
    [SerializeField, Range(0, 300)] private int echoDelayMilliseconds = 80;
    private const int JitterMilliseconds = 60;
    private const int NoiseFrameMilliseconds = 10;
    private const int MaxEchoReferenceMilliseconds = 500;
    [SerializeField, Range(0f, 2f)] private float playbackVolume = 1f;

    private static PlayerVoiceChat activeLocalVoice;

    private readonly object playbackLock = new();
    private readonly Dictionary<ushort, VoicePlaybackBuffer> playbackBuffers = new();
    private readonly Queue<float> echoReferenceSamples = new();

    private AudioSource voiceOutput;
    private AudioClip microphoneClip;
    private AudioClip playbackClip;
    private RnNoiseProcessor noiseProcessor;
    private WebRtcAecProcessor echoCanceller;
    private float[] microphoneFrame;
    private float[] echoReferenceFrame;
    private float[] echoCancelledFrame;
    private float[] cleanFrame;
    private float[] networkPacket;
    private int networkPacketPosition;
    private int voiceHangoverFrames;
    private float voiceGateGain;
    private float automaticGain = 1f;
    private float lastPlaybackSample;
    private int microphonePosition = -1;
    private int samplesPerPacket;

    private void Awake()
    {
        // The scene's original player is created before remote player copies.
        // Only that first instance owns the microphone and the mixed voice output.
        if (activeLocalVoice != null && activeLocalVoice != this)
        {
            enabled = false;
            return;
        }

        activeLocalVoice = this;
        packetMilliseconds = Mathf.Max(10, packetMilliseconds);
        voiceCloseThreshold = Mathf.Min(voiceCloseThreshold, voiceOpenThreshold);
        voiceOutput = GetComponent<AudioSource>();
        voiceOutput.playOnAwake = false;
        voiceOutput.loop = true;
        voiceOutput.spatialBlend = 0f;

        samplesPerPacket = Mathf.Max(1, RnNoiseProcessor.OutputSampleRate * packetMilliseconds / 1000);
        microphoneFrame = new float[RnNoiseProcessor.InputFrameSamples];
        echoReferenceFrame = new float[WebRtcAecProcessor.FrameSamples];
        echoCancelledFrame = new float[WebRtcAecProcessor.FrameSamples];
        cleanFrame = new float[RnNoiseProcessor.OutputFrameSamples];
        networkPacket = new float[samplesPerPacket];
    }

    private void Start()
    {
        if (!enabled)
            return;

        NetworkClient.GetInstance(ref networkClient);
        networkClient.SubscribeVoice(OnVoicePcmReceived);
        StartCoroutine(StartWhenGameReady());
    }

    private IEnumerator StartWhenGameReady()
    {
        yield return new WaitUntil(() => networkClient != null && networkClient.IsConnected);

        try
        {
            noiseProcessor = new RnNoiseProcessor();
            if (echoCancellation)
                echoCanceller = new WebRtcAecProcessor(echoDelayMilliseconds);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, this);
            enabled = false;
            yield break;
        }

        StartMicrophone();
        StartPlayback();
        Debug.Log("Voice chat started on the existing game connection.", this);
    }

    private void Update()
    {
        if (!enabled)
            return;

        CaptureMicrophone();
    }

    private void StartMicrophone()
    {
        string device = string.IsNullOrWhiteSpace(microphoneDevice) ? null : microphoneDevice;
        microphoneClip = Microphone.Start(device, true, 2, RnNoiseProcessor.InputSampleRate);
        if (microphoneClip == null)
        {
            Debug.LogError("Could not start the microphone.", this);
            return;
        }

        microphonePosition = Microphone.GetPosition(device);
    }

    private void CaptureMicrophone()
    {
        if (microphoneClip == null || networkClient == null || !networkClient.IsConnected)
            return;

        string device = string.IsNullOrWhiteSpace(microphoneDevice) ? null : microphoneDevice;
        int currentPosition = Microphone.GetPosition(device);
        if (currentPosition < 0)
            return;
        if (microphonePosition < 0)
        {
            microphonePosition = currentPosition;
            return;
        }

        int available = currentPosition >= microphonePosition
            ? currentPosition - microphonePosition
            : microphoneClip.samples - microphonePosition + currentPosition;

        while (available >= RnNoiseProcessor.InputFrameSamples)
        {
            microphoneClip.GetData(microphoneFrame, microphonePosition);
            microphonePosition = (microphonePosition + RnNoiseProcessor.InputFrameSamples) % microphoneClip.samples;
            available -= RnNoiseProcessor.InputFrameSamples;
            ProcessMicrophoneFrame();
        }
    }

    private void ProcessMicrophoneFrame()
    {
        float[] noiseInput = microphoneFrame;
        if (echoCanceller != null)
        {
            ReadEchoReferenceFrame();
            echoCanceller.Process(echoReferenceFrame, microphoneFrame, echoCancelledFrame);
            noiseInput = echoCancelledFrame;
        }

        float voiceProbability = noiseProcessor.Process(noiseInput, cleanFrame);
        ApplyVoiceGateAndAutomaticGain(voiceProbability);

        int sourcePosition = 0;
        while (sourcePosition < cleanFrame.Length)
        {
            int copyLength = Mathf.Min(cleanFrame.Length - sourcePosition, networkPacket.Length - networkPacketPosition);
            Array.Copy(cleanFrame, sourcePosition, networkPacket, networkPacketPosition, copyLength);
            sourcePosition += copyLength;
            networkPacketPosition += copyLength;

            if (networkPacketPosition != networkPacket.Length)
                continue;

            SendVoice(networkPacket);
            networkPacketPosition = 0;
        }
    }

    private void ApplyVoiceGateAndAutomaticGain(float voiceProbability)
    {
        int hangoverLength = Mathf.Max(1, voiceHangoverMilliseconds / NoiseFrameMilliseconds);
        if (voiceProbability >= voiceOpenThreshold)
        {
            voiceHangoverFrames = hangoverLength;
        }
        else if (voiceProbability >= voiceCloseThreshold && voiceHangoverFrames > 0)
        {
            voiceHangoverFrames = hangoverLength;
        }
        else if (voiceHangoverFrames > 0)
        {
            voiceHangoverFrames--;
        }

        bool voiceActive = voiceHangoverFrames > 0;
        float gateTarget = voiceActive ? 1f : 0f;
        float gateSpeed = voiceActive ? 1f : 0.2f;
        voiceGateGain = Mathf.MoveTowards(voiceGateGain, gateTarget, gateSpeed);

        if (voiceActive)
        {
            double squareSum = 0;
            for (int i = 0; i < cleanFrame.Length; i++)
                squareSum += cleanFrame[i] * cleanFrame[i];

            float rms = Mathf.Sqrt((float)(squareSum / cleanFrame.Length));
            if (rms > 0.0001f)
            {
                float desiredGain = targetVoiceLevel / (rms * microphoneGain);
                desiredGain = Mathf.Clamp(desiredGain, 0.1f, maximumAutomaticGain);

                // Reduce gain quickly on loud input, raise it slowly to avoid pumping noise.
                float smoothing = desiredGain < automaticGain ? 0.35f : 0.08f;
                automaticGain = Mathf.Lerp(automaticGain, desiredGain, smoothing);
            }
        }

        for (int i = 0; i < cleanFrame.Length; i++)
        {
            float amplified = cleanFrame[i] * microphoneGain * automaticGain * voiceGateGain;
            cleanFrame[i] = SoftLimit(amplified);
        }
    }

    private static float SoftLimit(float sample)
    {
        const float threshold = 0.9f;
        float magnitude = Mathf.Abs(sample);
        if (magnitude <= threshold)
            return sample;

        float limited = threshold + (1f - Mathf.Exp(-(magnitude - threshold) * 10f)) * (1f - threshold);
        return Mathf.Sign(sample) * Mathf.Min(limited, 0.999f);
    }

    private void SendVoice(float[] samples)
    {
        var pcm16 = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short pcm = (short)Mathf.RoundToInt(Mathf.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(i * 2, 2), pcm);
        }

        networkClient.SendVoicePcm(pcm16);
    }

    private void OnVoicePcmReceived(ushort senderSlot, byte[] pcm16)
    {
        if (pcm16 == null || pcm16.Length == 0 || pcm16.Length % 2 != 0)
            return;
        if (senderSlot == networkClient.peerSlot)
            return;

        int sampleCount = pcm16.Length / 2;
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            short pcm = BinaryPrimitives.ReadInt16LittleEndian(pcm16.AsSpan(i * 2, 2));
            samples[i] = pcm / 32768f;
        }

        lock (playbackLock)
        {
            if (!playbackBuffers.TryGetValue(senderSlot, out VoicePlaybackBuffer buffer))
            {
                buffer = new VoicePlaybackBuffer(
                    RnNoiseProcessor.OutputSampleRate * 2,
                    RnNoiseProcessor.OutputSampleRate * JitterMilliseconds / 1000);
                playbackBuffers.Add(senderSlot, buffer);
            }
            buffer.Write(samples);
        }
    }

    private void StartPlayback()
    {
        playbackClip = AudioClip.Create(
            "Network Voice",
            RnNoiseProcessor.OutputSampleRate,
            1,
            RnNoiseProcessor.OutputSampleRate,
            true,
            FillPlaybackBuffer);
        voiceOutput.clip = playbackClip;
        voiceOutput.Play();
    }

    private void FillPlaybackBuffer(float[] output)
    {
        Array.Clear(output, 0, output.Length);
        lock (playbackLock)
        {
            foreach (VoicePlaybackBuffer buffer in playbackBuffers.Values)
                buffer.ReadAndMix(output, playbackVolume);
        }

        for (int i = 0; i < output.Length; i++)
            output[i] = Mathf.Clamp(output[i], -1f, 1f);

        lock (playbackLock)
            QueueEchoReference(output);
    }

    private void QueueEchoReference(float[] playback)
    {
        int maximumSamples = WebRtcAecProcessor.SampleRate * MaxEchoReferenceMilliseconds / 1000;
        for (int i = 0; i < playback.Length; i++)
        {
            float current = playback[i];
            echoReferenceSamples.Enqueue(Mathf.Lerp(lastPlaybackSample, current, 1f / 3f));
            echoReferenceSamples.Enqueue(Mathf.Lerp(lastPlaybackSample, current, 2f / 3f));
            echoReferenceSamples.Enqueue(current);
            lastPlaybackSample = current;
        }

        while (echoReferenceSamples.Count > maximumSamples)
            echoReferenceSamples.Dequeue();
    }

    private void ReadEchoReferenceFrame()
    {
        lock (playbackLock)
        {
            for (int i = 0; i < echoReferenceFrame.Length; i++)
            {
                echoReferenceFrame[i] = echoReferenceSamples.Count > 0
                    ? echoReferenceSamples.Dequeue()
                    : 0f;
            }
        }
    }

    private void OnDestroy()
    {
        if (activeLocalVoice != this)
            return;

        activeLocalVoice = null;
        networkClient?.UnsubscribeVoice(OnVoicePcmReceived);
        string device = string.IsNullOrWhiteSpace(microphoneDevice) ? null : microphoneDevice;
        if (Microphone.IsRecording(device))
            Microphone.End(device);
        noiseProcessor?.Dispose();
        echoCanceller?.Dispose();
    }

    private sealed class VoicePlaybackBuffer
    {
        private const int FadeSamples = 64;
        private readonly float[] samples;
        private readonly int jitterSamples;
        private int readPosition;
        private int writePosition;
        private int count;
        private bool started;
        private int fadeInRemaining;
        private float lastSample;

        public VoicePlaybackBuffer(int capacity, int jitterSamples)
        {
            samples = new float[Mathf.Max(1, capacity)];
            this.jitterSamples = Mathf.Clamp(jitterSamples, 1, samples.Length);
        }

        public void Write(float[] input)
        {
            for (int i = 0; i < input.Length; i++)
            {
                if (count == samples.Length)
                {
                    readPosition = (readPosition + 1) % samples.Length;
                    count--;
                }

                samples[writePosition] = input[i];
                writePosition = (writePosition + 1) % samples.Length;
                count++;
            }
        }

        public void ReadAndMix(float[] output, float volume)
        {
            if (!started)
            {
                if (count < jitterSamples)
                    return;
                started = true;
                fadeInRemaining = FadeSamples;
            }

            for (int i = 0; i < output.Length; i++)
            {
                if (count == 0)
                {
                    int fadeLength = Mathf.Min(FadeSamples, output.Length - i);
                    for (int fade = 0; fade < fadeLength; fade++)
                    {
                        float gain = 1f - (fade + 1f) / fadeLength;
                        output[i + fade] += lastSample * gain * volume;
                    }
                    lastSample = 0f;
                    started = false;
                    return;
                }

                float sample = samples[readPosition];
                if (fadeInRemaining > 0)
                {
                    sample *= 1f - fadeInRemaining / (float)FadeSamples;
                    fadeInRemaining--;
                }
                output[i] += sample * volume;
                lastSample = sample;
                readPosition = (readPosition + 1) % samples.Length;
                count--;
            }
        }
    }
}
