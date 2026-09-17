using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

public enum AecDoubleTalkPreset
{
    Balanced,
    StrongEchoRemoval,
    StrongDoubleTalk,
    Custom
}

[RequireComponent(typeof(AudioSource))]
public sealed class PlayerVoiceChat : MonoBehaviour
{
    public event Action<float[], float[], float[], float[]> EchoDebugFrameProcessed;

    [SerializeField] private NetworkClient networkClient;
    [SerializeField] private string microphoneDevice;
    [SerializeField, Range(10, 40)] private int packetMilliseconds = 20;
    [SerializeField, Range(10, 100)] private int jitterMilliseconds = 40;
    [SerializeField, Range(1f, 8f)] private float microphoneGain = 3f;
    [SerializeField, Range(0f, 1f)] private float voiceOpenThreshold = 0.5f;
    [SerializeField, Range(0f, 1f)] private float voiceCloseThreshold = 0.35f;
    [SerializeField, Range(100, 500)] private int voiceHangoverMilliseconds = 250;
    [SerializeField] private bool useVoiceGate;
    [SerializeField] private bool protectDoubleTalk = true;
    [SerializeField, Range(0f, 1f)] private float doubleTalkOpenThreshold = 0.4f;
    [SerializeField, Range(0f, 1f)] private float doubleTalkCloseThreshold = 0.28f;
    [SerializeField, Range(250, 1000)] private int doubleTalkHangoverMilliseconds = 350;
    [SerializeField, Range(0.0005f, 0.05f)] private float farEndActivityThreshold = 0.003f;
    [SerializeField, Range(0.05f, 0.3f)] private float targetVoiceLevel = 0.16f;
    [SerializeField, Range(1f, 6f)] private float maximumAutomaticGain = 4f;
    [SerializeField] private bool echoCancellation = true;
    [SerializeField] private bool useSystemOutputEchoReference = true;
    [SerializeField] private bool automaticEchoDelay = true;
    [SerializeField, Range(0, 300)] private int echoDelayMilliseconds = 80;
    [SerializeField] private AecDoubleTalkPreset echoDoubleTalkPreset = AecDoubleTalkPreset.Balanced;
    [SerializeField, Range(0.05f, 2f)] private float customNearEndThreshold = 0.25f;
    [SerializeField, Range(1f, 100f)] private float customNearEndSnrThreshold = 30f;
    [SerializeField, Range(4, 1000)] private int customNearEndTriggerMilliseconds = 48;
    [SerializeField, Range(4, 4000)] private int customNearEndHoldMilliseconds = 200;
    [SerializeField, Range(1f, 100f)] private float customNearEndExitThreshold = 10f;
    private const int NoiseFrameMilliseconds = 10;
    private const int MaxEchoReferenceMilliseconds = 500;
    [SerializeField, Range(0f, 2f)] private float playbackVolume = 1f;

    private static PlayerVoiceChat activeLocalVoice;

    private readonly object playbackBufferRegistrationLock = new();
    private readonly Dictionary<ushort, VoicePlaybackBuffer> playbackBuffers = new();
    private volatile VoicePlaybackBuffer[] playbackBufferSnapshot = Array.Empty<VoicePlaybackBuffer>();
    private readonly ConcurrentQueue<float> echoReferenceSamples = new();
    private int echoReferenceSampleCount;

    private AudioSource voiceOutput;
    private AudioClip microphoneClip;
    private AudioClip playbackClip;
    private volatile bool shuttingDown;
    private RnNoiseProcessor noiseProcessor;
    private WebRtcAecProcessor echoCanceller;
    private WasapiLoopbackCapture systemOutputCapture;
    private float[] microphoneFrame;
    private float[] echoReferenceFrame;
    private float[] unityEchoReferenceFrame;
    private float[] echoCancelledFrame;
    private float[] cleanFrame;
    private float[] networkPacket;
    private int networkPacketPosition;
    private int voiceHangoverFrames;
    private float voiceGateGain;
    private float automaticGain = 1f;
    private int microphonePosition = -1;
    private int samplesPerPacket;
    private int playbackOutputSampleRate;
    private bool appliedEchoCancellation;
    private bool farEndActive;
    private int appliedEchoConfigurationHash = int.MinValue;
    private float nextVoiceHealthLogTime;
    private int loggedVoiceUnderruns;

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
        unityEchoReferenceFrame = new float[WebRtcAecProcessor.FrameSamples];
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
            ApplyEchoCancellationSetting();
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

        if (appliedEchoCancellation != echoCancellation ||
            appliedEchoConfigurationHash != CalculateEchoConfigurationHash())
            ApplyEchoCancellationSetting();
        CaptureMicrophone();
        LogVoiceHealthIfNeeded();
    }

    public void SetEchoCancellation(bool enabled)
    {
        echoCancellation = enabled;
        if (noiseProcessor != null && appliedEchoCancellation != echoCancellation)
            ApplyEchoCancellationSetting();
    }

    public void SetEchoDoubleTalkPreset(AecDoubleTalkPreset preset)
    {
        echoDoubleTalkPreset = preset;
        if (noiseProcessor != null)
            ApplyEchoCancellationSetting();
    }

    public void SetCustomEchoDoubleTalkSettings(
        float nearEndThreshold,
        float nearEndSnrThreshold,
        int nearEndTriggerMilliseconds,
        int nearEndHoldMilliseconds,
        float nearEndExitThreshold)
    {
        echoDoubleTalkPreset = AecDoubleTalkPreset.Custom;
        customNearEndThreshold = Mathf.Clamp(nearEndThreshold, 0.05f, 2f);
        customNearEndSnrThreshold = Mathf.Clamp(nearEndSnrThreshold, 1f, 100f);
        customNearEndTriggerMilliseconds = Mathf.Clamp(nearEndTriggerMilliseconds, 4, 1000);
        customNearEndHoldMilliseconds = Mathf.Clamp(nearEndHoldMilliseconds, 4, 4000);
        customNearEndExitThreshold = Mathf.Clamp(nearEndExitThreshold, 1f, 100f);
        if (noiseProcessor != null)
            ApplyEchoCancellationSetting();
    }

    private void ApplyEchoCancellationSetting()
    {
        systemOutputCapture?.Dispose();
        systemOutputCapture = null;
        echoCanceller?.Dispose();
        echoCanceller = null;

        while (echoReferenceSamples.TryDequeue(out _))
            Interlocked.Decrement(ref echoReferenceSampleCount);

        if (echoCancellation)
        {
            AecTuning tuning = GetAecTuning();
            echoCanceller = new WebRtcAecProcessor(
                automaticEchoDelay ? null : echoDelayMilliseconds,
                tuning.nearEndThreshold,
                tuning.nearEndSnrThreshold,
                tuning.nearEndTriggerMilliseconds,
                tuning.nearEndHoldMilliseconds,
                tuning.nearEndExitThreshold);

            if (useSystemOutputEchoReference &&
                (Application.platform == RuntimePlatform.WindowsEditor ||
                 Application.platform == RuntimePlatform.WindowsPlayer))
            {
                try
                {
                    systemOutputCapture = new WasapiLoopbackCapture();
                    Debug.Log("AEC is using the Windows system output reference.", this);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        $"System output capture is unavailable. " +
                        $"AEC will use the Unity voice reference instead. {exception.Message}",
                        this);
                }
            }
        }

        appliedEchoCancellation = echoCancellation;
        appliedEchoConfigurationHash = CalculateEchoConfigurationHash();
        Debug.Log($"Echo cancellation {(echoCancellation ? "enabled" : "disabled")}.", this);
    }

    private AecTuning GetAecTuning()
    {
        switch (echoDoubleTalkPreset)
        {
            case AecDoubleTalkPreset.StrongEchoRemoval:
                return new AecTuning(0.2f, 30f, 48, 200, 8f);
            case AecDoubleTalkPreset.StrongDoubleTalk:
                return new AecTuning(0.5f, 15f, 24, 400, 15f);
            case AecDoubleTalkPreset.Custom:
                return new AecTuning(
                    customNearEndThreshold,
                    customNearEndSnrThreshold,
                    customNearEndTriggerMilliseconds,
                    customNearEndHoldMilliseconds,
                    customNearEndExitThreshold);
            default:
                return new AecTuning(0.25f, 30f, 48, 200, 10f);
        }
    }

    private int CalculateEchoConfigurationHash()
    {
        AecTuning tuning = GetAecTuning();
        unchecked
        {
            int hash = (int)echoDoubleTalkPreset;
            hash = hash * 31 + tuning.nearEndThreshold.GetHashCode();
            hash = hash * 31 + tuning.nearEndSnrThreshold.GetHashCode();
            hash = hash * 31 + tuning.nearEndTriggerMilliseconds;
            hash = hash * 31 + tuning.nearEndHoldMilliseconds;
            hash = hash * 31 + tuning.nearEndExitThreshold.GetHashCode();
            hash = hash * 31 + automaticEchoDelay.GetHashCode();
            hash = hash * 31 + echoDelayMilliseconds;
            hash = hash * 31 + useSystemOutputEchoReference.GetHashCode();
            return hash;
        }
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
            ReadEchoReferenceFrame(unityEchoReferenceFrame);
            if (systemOutputCapture != null)
                systemOutputCapture.ReadFrame(echoReferenceFrame);
            else
                Array.Copy(unityEchoReferenceFrame, echoReferenceFrame, echoReferenceFrame.Length);

            farEndActive = protectDoubleTalk &&
                CalculateRms(echoReferenceFrame) >= farEndActivityThreshold;
            echoCanceller.Process(echoReferenceFrame, microphoneFrame, echoCancelledFrame);
            EchoDebugFrameProcessed?.Invoke(
                unityEchoReferenceFrame,
                echoReferenceFrame,
                microphoneFrame,
                echoCancelledFrame);
            noiseInput = echoCancelledFrame;
        }
        else
        {
            farEndActive = false;
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
        float openThreshold = farEndActive
            ? Mathf.Min(voiceOpenThreshold, doubleTalkOpenThreshold)
            : voiceOpenThreshold;
        float closeThreshold = farEndActive
            ? Mathf.Min(voiceCloseThreshold, doubleTalkCloseThreshold)
            : voiceCloseThreshold;
        int hangoverMilliseconds = farEndActive
            ? Mathf.Max(voiceHangoverMilliseconds, doubleTalkHangoverMilliseconds)
            : voiceHangoverMilliseconds;
        int hangoverLength = Mathf.Max(1, hangoverMilliseconds / NoiseFrameMilliseconds);

        if (voiceProbability >= openThreshold)
        {
            voiceHangoverFrames = hangoverLength;
        }
        else if (voiceProbability >= closeThreshold && voiceHangoverFrames > 0)
        {
            voiceHangoverFrames = hangoverLength;
        }
        else if (voiceHangoverFrames > 0)
        {
            voiceHangoverFrames--;
        }

        bool voiceActive = voiceHangoverFrames > 0;
        float gateTarget = !useVoiceGate || voiceActive ? 1f : 0f;
        float gateSpeed = voiceActive ? 1f : 0.2f;
        float previousGateGain = voiceGateGain;
        voiceGateGain = Mathf.MoveTowards(voiceGateGain, gateTarget, gateSpeed);
        float previousAutomaticGain = automaticGain;

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
            float framePosition = (i + 1f) / cleanFrame.Length;
            float smoothGateGain = Mathf.Lerp(previousGateGain, voiceGateGain, framePosition);
            float smoothAutomaticGain = Mathf.Lerp(previousAutomaticGain, automaticGain, framePosition);
            float amplified = cleanFrame[i] * microphoneGain * smoothAutomaticGain * smoothGateGain;
            cleanFrame[i] = SoftLimit(amplified);
        }
    }

    private static float CalculateRms(float[] samples)
    {
        double squareSum = 0;
        for (int i = 0; i < samples.Length; i++)
            squareSum += samples[i] * samples[i];
        return Mathf.Sqrt((float)(squareSum / samples.Length));
    }

    private void LogVoiceHealthIfNeeded()
    {
        if (Time.unscaledTime < nextVoiceHealthLogTime)
            return;
        nextVoiceHealthLogTime = Time.unscaledTime + 2f;

        int underruns = 0;
        VoicePlaybackBuffer[] buffers = playbackBufferSnapshot;
        for (int i = 0; i < buffers.Length; i++)
            underruns += buffers[i].UnderrunCount;

        if (underruns == loggedVoiceUnderruns)
            return;

        Debug.LogWarning(
            $"Voice stream interruptions: receive underruns={underruns} " +
            $"(+{underruns - loggedVoiceUnderruns}).",
            this);
        loggedVoiceUnderruns = underruns;
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
        if (shuttingDown)
            return;
        if (pcm16 == null || pcm16.Length == 0 || pcm16.Length % 2 != 0)
            return;
        if (senderSlot == networkClient.peerSlot)
            return;

        VoicePlaybackBuffer buffer;
        lock (playbackBufferRegistrationLock)
        {
            if (!playbackBuffers.TryGetValue(senderSlot, out buffer))
            {
                buffer = new VoicePlaybackBuffer(
                    RnNoiseProcessor.OutputSampleRate * 2,
                    RnNoiseProcessor.OutputSampleRate * jitterMilliseconds / 1000);
                playbackBuffers.Add(senderSlot, buffer);
                playbackBufferSnapshot = new List<VoicePlaybackBuffer>(playbackBuffers.Values).ToArray();
            }
        }
        buffer.WritePcm16(pcm16);
    }

    private void StartPlayback()
    {
        playbackOutputSampleRate = AudioSettings.outputSampleRate;
        AudioSettings.GetDSPBufferSize(out int dspBufferSamples, out int dspBufferCount);
        Debug.Log(
            $"Voice latency settings: packet={packetMilliseconds}ms, " +
            $"jitter={jitterMilliseconds}ms, output={playbackOutputSampleRate}Hz, " +
            $"DSP={dspBufferSamples} samples x {dspBufferCount}.",
            this);

        playbackClip = AudioClip.Create(
            "Network Voice Silence",
            playbackOutputSampleRate,
            1,
            playbackOutputSampleRate,
            false);
        voiceOutput.clip = playbackClip;
        voiceOutput.Play();
    }

    private void OnAudioFilterRead(float[] output, int channels)
    {
        Array.Clear(output, 0, output.Length);
        if (shuttingDown || playbackOutputSampleRate <= 0 || channels <= 0)
            return;

        VoicePlaybackBuffer[] buffers = playbackBufferSnapshot;
        for (int i = 0; i < buffers.Length; i++)
            buffers[i].ReadAndMix(output, channels, playbackOutputSampleRate, playbackVolume);

        for (int i = 0; i < output.Length; i++)
            output[i] = SoftLimit(output[i]);

        if (echoCancellation)
            QueueEchoReference(output, channels);
    }

    private void QueueEchoReference(float[] playback, int channels)
    {
        int maximumSamples = WebRtcAecProcessor.SampleRate * MaxEchoReferenceMilliseconds / 1000;
        for (int frame = 0; frame < playback.Length / channels; frame++)
        {
            float current = 0f;
            int frameOffset = frame * channels;
            for (int channel = 0; channel < channels; channel++)
                current += playback[frameOffset + channel];
            current /= channels;
            echoReferenceSamples.Enqueue(current);
            Interlocked.Increment(ref echoReferenceSampleCount);
        }

        while (Volatile.Read(ref echoReferenceSampleCount) > maximumSamples &&
               echoReferenceSamples.TryDequeue(out _))
        {
            Interlocked.Decrement(ref echoReferenceSampleCount);
        }
    }

    private void ReadEchoReferenceFrame(float[] output)
    {
        for (int i = 0; i < output.Length; i++)
        {
            if (echoReferenceSamples.TryDequeue(out float sample))
            {
                Interlocked.Decrement(ref echoReferenceSampleCount);
                output[i] = sample;
            }
            else
                output[i] = 0f;
        }
    }

    private void OnDestroy()
    {
        shuttingDown = true;
        if (activeLocalVoice != this)
            return;

        activeLocalVoice = null;
        networkClient?.UnsubscribeVoice(OnVoicePcmReceived);
        string device = string.IsNullOrWhiteSpace(microphoneDevice) ? null : microphoneDevice;
        if (Microphone.IsRecording(device))
            Microphone.End(device);
        noiseProcessor?.Dispose();
        systemOutputCapture?.Dispose();
        echoCanceller?.Dispose();
    }

    private readonly struct AecTuning
    {
        public readonly float nearEndThreshold;
        public readonly float nearEndSnrThreshold;
        public readonly int nearEndTriggerMilliseconds;
        public readonly int nearEndHoldMilliseconds;
        public readonly float nearEndExitThreshold;

        public AecTuning(
            float nearEndThreshold,
            float nearEndSnrThreshold,
            int nearEndTriggerMilliseconds,
            int nearEndHoldMilliseconds,
            float nearEndExitThreshold)
        {
            this.nearEndThreshold = nearEndThreshold;
            this.nearEndSnrThreshold = nearEndSnrThreshold;
            this.nearEndTriggerMilliseconds = nearEndTriggerMilliseconds;
            this.nearEndHoldMilliseconds = nearEndHoldMilliseconds;
            this.nearEndExitThreshold = nearEndExitThreshold;
        }
    }

    private sealed class VoicePlaybackBuffer
    {
        private const int FadeMilliseconds = 5;
        private const int AdjustmentMilliseconds = 10;
        private const int MaximumExtraJitterMilliseconds = 60;
        private const int StableSecondsBeforeReduction = 5;
        private readonly float[] samples;
        private readonly int minimumJitterSamples;
        private readonly int maximumJitterSamples;
        private readonly int adjustmentSamples;
        private readonly int stableSamplesBeforeReduction;
        private long readSequence;
        private long writeSequence;
        private int targetJitterSamples;
        private int stableSamples;
        private bool started;
        private int fadeInRemaining;
        private double resamplePosition;
        private float currentSample;
        private float nextSample;
        private float lastSample;
        private int underrunCount;

        public int UnderrunCount => Volatile.Read(ref underrunCount);

        public VoicePlaybackBuffer(int capacity, int jitterSamples)
        {
            samples = new float[Math.Max(1, capacity)];
            minimumJitterSamples = Math.Min(Math.Max(jitterSamples, 1), samples.Length);
            adjustmentSamples = Math.Max(1, RnNoiseProcessor.OutputSampleRate * AdjustmentMilliseconds / 1000);
            maximumJitterSamples = Math.Min(
                samples.Length,
                minimumJitterSamples + RnNoiseProcessor.OutputSampleRate * MaximumExtraJitterMilliseconds / 1000);
            stableSamplesBeforeReduction = RnNoiseProcessor.OutputSampleRate * StableSecondsBeforeReduction;
            targetJitterSamples = minimumJitterSamples;
        }

        public void WritePcm16(byte[] pcm16)
        {
            int sampleCount = pcm16.Length / 2;
            long write = Volatile.Read(ref writeSequence);
            long read = Volatile.Read(ref readSequence);
            int writableSamples = Math.Min(sampleCount, samples.Length - (int)(write - read));

            for (int i = 0; i < writableSamples; i++)
            {
                short pcm = BinaryPrimitives.ReadInt16LittleEndian(pcm16.AsSpan(i * 2, 2));
                samples[(int)((write + i) % samples.Length)] = pcm / 32768f;
            }

            Volatile.Write(ref writeSequence, write + writableSamples);
        }

        public void ReadAndMix(float[] output, int channels, int outputSampleRate, float volume)
        {
            if (!started)
            {
                if (AvailableSamples < targetJitterSamples + 2)
                    return;
                started = true;
                fadeInRemaining = Math.Max(1, outputSampleRate * FadeMilliseconds / 1000);
                resamplePosition = 0;
                currentSample = ReadSample();
                nextSample = ReadSample();
            }

            int outputFrames = output.Length / channels;
            double sourceStep = RnNoiseProcessor.OutputSampleRate / (double)outputSampleRate;
            for (int frame = 0; frame < outputFrames; frame++)
            {
                float sample = currentSample + (nextSample - currentSample) * (float)resamplePosition;
                if (fadeInRemaining > 0)
                {
                    int fadeFrames = Math.Max(1, outputSampleRate * FadeMilliseconds / 1000);
                    sample *= 1f - fadeInRemaining / (float)fadeFrames;
                    fadeInRemaining--;
                }

                int outputOffset = frame * channels;
                for (int channel = 0; channel < channels; channel++)
                    output[outputOffset + channel] += sample * volume;
                lastSample = sample;

                resamplePosition += sourceStep;
                while (resamplePosition >= 1.0)
                {
                    resamplePosition -= 1.0;
                    currentSample = nextSample;
                    if (AvailableSamples == 0)
                    {
                        Interlocked.Increment(ref underrunCount);
                        FadeOut(output, outputOffset + channels, channels, outputSampleRate, volume);
                        started = false;
                        stableSamples = 0;
                        targetJitterSamples = Math.Min(
                            maximumJitterSamples,
                            targetJitterSamples + adjustmentSamples);
                        return;
                    }

                    nextSample = ReadSample();
                }
            }
        }

        private float ReadSample()
        {
            long read = Volatile.Read(ref readSequence);
            float sample = samples[(int)(read % samples.Length)];
            Volatile.Write(ref readSequence, read + 1);
            stableSamples++;

            if (stableSamples >= stableSamplesBeforeReduction &&
                targetJitterSamples > minimumJitterSamples)
            {
                targetJitterSamples = Math.Max(
                    minimumJitterSamples,
                    targetJitterSamples - adjustmentSamples);
                stableSamples = 0;
            }

            return sample;
        }

        private int AvailableSamples
        {
            get
            {
                long available = Volatile.Read(ref writeSequence) - Volatile.Read(ref readSequence);
                return available <= 0 ? 0 : (int)Math.Min(available, samples.Length);
            }
        }

        private void FadeOut(
            float[] output,
            int outputOffset,
            int channels,
            int outputSampleRate,
            float volume)
        {
            int remainingFrames = (output.Length - outputOffset) / channels;
            int fadeFrames = Math.Min(
                Math.Max(1, FadeMilliseconds * outputSampleRate / 1000),
                remainingFrames);

            for (int frame = 0; frame < fadeFrames; frame++)
            {
                float gain = 1f - (frame + 1f) / fadeFrames;
                for (int channel = 0; channel < channels; channel++)
                    output[outputOffset + frame * channels + channel] += lastSample * gain * volume;
            }
            lastSample = 0f;
        }
    }
}
