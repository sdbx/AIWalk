using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

[Serializable]
public sealed class RecordedVoiceAudioPayload
{
    public string encoding;
    public int sampleRate;
    public int channels;
    public string pcmBase64;
}

[Serializable]
public sealed class RecordedVoiceAudio
{
    public readonly byte[] Pcm16;
    public readonly int SampleRate;
    public readonly int Channels;

    public float DurationSeconds => Pcm16.Length / 2f / Channels / SampleRate;

    public string ToBase64()
    {
        return Convert.ToBase64String(Pcm16);
    }

    public RecordedVoiceAudioPayload ToServerData()
    {
        return new RecordedVoiceAudioPayload
        {
            encoding = "pcm_s16le",
            sampleRate = SampleRate,
            channels = Channels,
            pcmBase64 = ToBase64()
        };
    }

    public RecordedVoiceAudio(byte[] pcm16, int sampleRate, int channels)
    {
        Pcm16 = pcm16;
        SampleRate = sampleRate;
        Channels = channels;
    }
}

public sealed class IngameMicrophone : MonoBehaviour
{
    private const int SampleRate = 16000;

    [SerializeField] private Transform listeningPosition;
    [SerializeField] private AudioSource outputAudioSource;
    [SerializeField] private bool includeLocalNetworkVoice;
    [SerializeField, Range(0, 100)] private int jitterMilliseconds = 40;
    [SerializeField, Range(0f, 4f)] private float listenerGain = 1f;
    [SerializeField, Range(0f, 4f)] private float recordingGain = 1f;

    private readonly object sourceLock = new();
    private readonly Dictionary<NetworkVoiceSpeaker, SourceState> sources = new();
    private readonly List<float> recordedSamples = new();
    private volatile SourceEntry[] sourceSnapshot = Array.Empty<SourceEntry>();
    private volatile bool isRecording;
    private double lastCaptureTime;
    private double fractionalSamples;
    private float[] mixBuffer = Array.Empty<float>();
    private NetworkVoiceAudioOutput audioOutput;
    private AudioClip outputClip;

    public bool IsRecording => isRecording;
    public bool IncludeLocalNetworkVoice
    {
        get => includeLocalNetworkVoice;
        set => includeLocalNetworkVoice = value;
    }

    private void Awake()
    {
        if (outputAudioSource == null)
            return;

        outputAudioSource.playOnAwake = false;
        outputAudioSource.loop = true;
        outputAudioSource.spatialBlend = 0f;
        outputAudioSource.panStereo = 0f;
        outputAudioSource.dopplerLevel = 0f;
        outputAudioSource.reverbZoneMix = 0f;
        outputAudioSource.bypassEffects = true;
        outputAudioSource.bypassListenerEffects = true;
        outputAudioSource.bypassReverbZones = true;

        int outputSampleRate = AudioSettings.outputSampleRate;
        outputClip = AudioClip.Create(
            $"Voice Listener Output - {name}",
            outputSampleRate,
            2,
            outputSampleRate,
            false);
        outputAudioSource.clip = outputClip;
        audioOutput = outputAudioSource.gameObject.AddComponent<NetworkVoiceAudioOutput>();
        audioOutput.Configure(RenderOutput, outputSampleRate);
    }

    private void OnEnable()
    {
        NetworkVoiceSpeaker.SpeakerEnabled += AddSpeaker;
        NetworkVoiceSpeaker.SpeakerDisabled += RemoveSpeaker;

        NetworkVoiceSpeaker[] speakers = NetworkVoiceSpeaker.GetActiveSpeakers();
        for (int i = 0; i < speakers.Length; i++)
            AddSpeaker(speakers[i]);

        if (outputAudioSource != null && !outputAudioSource.isPlaying)
            outputAudioSource.Play();
    }

    private void Update()
    {
        UpdateSpatialMix();
        if (isRecording)
            CaptureElapsedAudio();
    }

    public void StartRecording()
    {
        recordedSamples.Clear();
        SourceState[] snapshot = GetSourceSnapshot();
        for (int i = 0; i < snapshot.Length; i++)
            snapshot[i].ResetRecording(jitterMilliseconds);

        fractionalSamples = 0;
        lastCaptureTime = AudioSettings.dspTime;
        isRecording = true;
    }

    public RecordedVoiceAudio StopRecording()
    {
        if (isRecording)
            CaptureElapsedAudio();
        isRecording = false;

        var pcm16 = new byte[recordedSamples.Count * 2];
        for (int i = 0; i < recordedSamples.Count; i++)
        {
            short sample = (short)Mathf.RoundToInt(
                Mathf.Clamp(recordedSamples[i], -1f, 1f) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(i * 2, 2), sample);
        }
        return new RecordedVoiceAudio(pcm16, SampleRate, 1);
    }

    public Coroutine RecordForSeconds(float seconds, Action<RecordedVoiceAudio> completed)
    {
        return StartCoroutine(RecordForSecondsRoutine(Mathf.Max(0f, seconds), completed));
    }

    private IEnumerator RecordForSecondsRoutine(float seconds, Action<RecordedVoiceAudio> completed)
    {
        StartRecording();
        yield return new WaitForSecondsRealtime(seconds);
        completed?.Invoke(StopRecording());
    }

    public void AddSpeaker(NetworkVoiceSpeaker speaker)
    {
        if (speaker == null)
            return;

        lock (sourceLock)
        {
            if (sources.ContainsKey(speaker))
                return;
            sources.Add(speaker, new SourceState(jitterMilliseconds));
            RebuildSourceSnapshot();
        }
        speaker.AddListener(this);
    }

    public void RemoveSpeaker(NetworkVoiceSpeaker speaker)
    {
        if (speaker == null)
            return;
        speaker.RemoveListener(this);
        lock (sourceLock)
        {
            sources.Remove(speaker);
            RebuildSourceSnapshot();
        }
    }

    internal void ReceivePcm(
        NetworkVoiceSpeaker speaker,
        byte[] pcm16,
        bool isLocalMicrophone)
    {
        if (isLocalMicrophone && !includeLocalNetworkVoice)
            return;

        SourceState state;
        lock (sourceLock)
        {
            if (!sources.TryGetValue(speaker, out state))
                return;
        }
        state.Write(pcm16, isRecording);
    }

    private void UpdateSpatialMix()
    {
        Transform listenerTransform = listeningPosition != null
            ? listeningPosition
            : transform;
        Vector3 listenerPosition = listenerTransform.position;
        Vector3 listenerRight = listenerTransform.right;

        SourceEntry[] snapshot = sourceSnapshot;
        for (int i = 0; i < snapshot.Length; i++)
        {
            NetworkVoiceSpeaker speaker = snapshot[i].Speaker;
            if (speaker == null || !speaker.isActiveAndEnabled)
            {
                snapshot[i].State.SetSpatialGains(0f, 0f, 0f);
                continue;
            }

            float monoGain = speaker.GetVolumeAt(listenerPosition) * listenerGain;
            Vector3 offset = speaker.SoundPosition - listenerPosition;
            float pan = offset.sqrMagnitude > 0.000001f
                ? Mathf.Clamp(Vector3.Dot(listenerRight, offset.normalized), -1f, 1f)
                : 0f;
            float leftGain = monoGain * Mathf.Sqrt((1f - pan) * 0.5f);
            float rightGain = monoGain * Mathf.Sqrt((1f + pan) * 0.5f);
            snapshot[i].State.SetSpatialGains(monoGain, leftGain, rightGain);
        }
    }

    private void RenderOutput(float[] output, int channels, int outputSampleRate)
    {
        SourceEntry[] snapshot = sourceSnapshot;
        for (int i = 0; i < snapshot.Length; i++)
            snapshot[i].State.ReadAndMixOutput(output, channels, outputSampleRate);

        for (int i = 0; i < output.Length; i++)
            output[i] = SoftLimit(output[i]);
    }

    private void CaptureElapsedAudio()
    {
        double now = AudioSettings.dspTime;
        double exactSamples = (now - lastCaptureTime) * SampleRate + fractionalSamples;
        int sampleCount = Math.Min(SampleRate, Math.Max(0, (int)exactSamples));
        fractionalSamples = exactSamples - sampleCount;
        lastCaptureTime = now;
        if (sampleCount == 0)
            return;

        if (mixBuffer.Length != sampleCount)
            mixBuffer = new float[sampleCount];
        else
            Array.Clear(mixBuffer, 0, mixBuffer.Length);

        SourceEntry[] snapshot = sourceSnapshot;
        for (int i = 0; i < snapshot.Length; i++)
            snapshot[i].State.ReadAndMixRecording(mixBuffer, recordingGain);

        for (int i = 0; i < mixBuffer.Length; i++)
            recordedSamples.Add(SoftLimit(mixBuffer[i]));
    }

    private SourceState[] GetSourceSnapshot()
    {
        lock (sourceLock)
        {
            var result = new SourceState[sources.Count];
            sources.Values.CopyTo(result, 0);
            return result;
        }
    }

    private void RebuildSourceSnapshot()
    {
        var result = new SourceEntry[sources.Count];
        int index = 0;
        foreach (KeyValuePair<NetworkVoiceSpeaker, SourceState> pair in sources)
            result[index++] = new SourceEntry(pair.Key, pair.Value);
        sourceSnapshot = result;
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

    private void OnDisable()
    {
        NetworkVoiceSpeaker.SpeakerEnabled -= AddSpeaker;
        NetworkVoiceSpeaker.SpeakerDisabled -= RemoveSpeaker;

        NetworkVoiceSpeaker[] speakers;
        lock (sourceLock)
        {
            speakers = new NetworkVoiceSpeaker[sources.Count];
            sources.Keys.CopyTo(speakers, 0);
            sources.Clear();
            sourceSnapshot = Array.Empty<SourceEntry>();
        }
        for (int i = 0; i < speakers.Length; i++)
            speakers[i]?.RemoveListener(this);
        isRecording = false;
        if (outputAudioSource != null)
            outputAudioSource.Stop();
    }

    private void OnDestroy()
    {
        if (audioOutput != null)
            Destroy(audioOutput);
        if (outputClip != null)
            Destroy(outputClip);
    }

    private sealed class SourceState
    {
        private NetworkVoicePlaybackBuffer outputBuffer;
        private NetworkVoicePlaybackBuffer recordingBuffer;
        private float monoGain;
        private float leftGain;
        private float rightGain;

        public SourceState(int jitterMs)
        {
            outputBuffer = CreateBuffer(jitterMs);
            recordingBuffer = CreateBuffer(jitterMs);
        }

        public void ResetRecording(int jitterMs)
        {
            Volatile.Write(ref recordingBuffer, CreateBuffer(jitterMs));
        }

        public void Write(byte[] pcm16, bool record)
        {
            Volatile.Read(ref outputBuffer).WritePcm16(pcm16);
            if (record)
                Volatile.Read(ref recordingBuffer).WritePcm16(pcm16);
        }

        public void SetSpatialGains(float mono, float left, float right)
        {
            Volatile.Write(ref monoGain, mono);
            Volatile.Write(ref leftGain, left);
            Volatile.Write(ref rightGain, right);
        }

        public void ReadAndMixOutput(float[] output, int channels, int outputSampleRate)
        {
            Volatile.Read(ref outputBuffer).ReadAndMixStereo(
                output,
                channels,
                outputSampleRate,
                Volatile.Read(ref leftGain),
                Volatile.Read(ref rightGain));
        }

        public void ReadAndMixRecording(float[] output, float gain)
        {
            Volatile.Read(ref recordingBuffer).ReadAndMix(
                output,
                1,
                SampleRate,
                Volatile.Read(ref monoGain) * gain);
        }

        private static NetworkVoicePlaybackBuffer CreateBuffer(int jitterMs)
        {
            return new NetworkVoicePlaybackBuffer(
                SampleRate * 2,
                Mathf.Max(1, SampleRate * jitterMs / 1000));
        }
    }

    private readonly struct SourceEntry
    {
        public readonly NetworkVoiceSpeaker Speaker;
        public readonly SourceState State;

        public SourceEntry(NetworkVoiceSpeaker speaker, SourceState state)
        {
            Speaker = speaker;
            State = state;
        }
    }
}
