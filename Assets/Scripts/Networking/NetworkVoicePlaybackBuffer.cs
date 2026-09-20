using System;
using System.Buffers.Binary;
using System.Threading;

public sealed class NetworkVoicePlaybackBuffer
{
    private const int InputSampleRate = 16000;
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
    private bool started;
    private int fadeInRemaining;
    private double resamplePosition;
    private float currentSample;
    private float nextSample;
    private float lastSample;
    private int underrunCount;
    private int targetJitterSamples;
    private int stableSamples;

    public int UnderrunCount => Volatile.Read(ref underrunCount);

    public NetworkVoicePlaybackBuffer(int capacitySamples, int initialJitterSamples)
    {
        samples = new float[Math.Max(1, capacitySamples)];
        minimumJitterSamples = Math.Min(Math.Max(initialJitterSamples, 1), samples.Length);
        adjustmentSamples = Math.Max(1, InputSampleRate * AdjustmentMilliseconds / 1000);
        maximumJitterSamples = Math.Min(
            samples.Length,
            minimumJitterSamples + InputSampleRate * MaximumExtraJitterMilliseconds / 1000);
        stableSamplesBeforeReduction = InputSampleRate * StableSecondsBeforeReduction;
        targetJitterSamples = minimumJitterSamples;
    }

    public void WritePcm16(byte[] pcm16)
    {
        if (pcm16 == null || pcm16.Length < 2)
            return;

        int sampleCount = pcm16.Length / 2;
        long write = Volatile.Read(ref writeSequence);
        long read = Volatile.Read(ref readSequence);
        int writable = Math.Min(sampleCount, samples.Length - (int)Math.Min(write - read, samples.Length));

        for (int i = 0; i < writable; i++)
        {
            short pcm = BinaryPrimitives.ReadInt16LittleEndian(pcm16.AsSpan(i * 2, 2));
            samples[(int)((write + i) % samples.Length)] = pcm / 32768f;
        }

        Volatile.Write(ref writeSequence, write + writable);
    }

    public void ReadAndMix(float[] output, int channels, int outputSampleRate, float volume)
    {
        if (channels <= 0 || outputSampleRate <= 0)
            return;

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

        int frames = output.Length / channels;
        double sourceStep = InputSampleRate / (double)outputSampleRate;
        for (int frame = 0; frame < frames; frame++)
        {
            float sample = currentSample + (nextSample - currentSample) * (float)resamplePosition;
            if (fadeInRemaining > 0)
            {
                int fadeFrames = Math.Max(1, outputSampleRate * FadeMilliseconds / 1000);
                sample *= 1f - fadeInRemaining / (float)fadeFrames;
                fadeInRemaining--;
            }

            int offset = frame * channels;
            for (int channel = 0; channel < channels; channel++)
                output[offset + channel] += sample * volume;
            lastSample = sample;

            resamplePosition += sourceStep;
            while (resamplePosition >= 1.0)
            {
                resamplePosition -= 1.0;
                currentSample = nextSample;
                if (AvailableSamples == 0)
                {
                    Interlocked.Increment(ref underrunCount);
                    FadeOut(output, offset + channels, channels, outputSampleRate, volume);
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

    public void ReadAndMixStereo(
        float[] output,
        int channels,
        int outputSampleRate,
        float leftVolume,
        float rightVolume)
    {
        if (channels <= 0 || outputSampleRate <= 0)
            return;

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

        int frames = output.Length / channels;
        double sourceStep = InputSampleRate / (double)outputSampleRate;
        for (int frame = 0; frame < frames; frame++)
        {
            float sample = currentSample + (nextSample - currentSample) * (float)resamplePosition;
            if (fadeInRemaining > 0)
            {
                int fadeFrames = Math.Max(1, outputSampleRate * FadeMilliseconds / 1000);
                sample *= 1f - fadeInRemaining / (float)fadeFrames;
                fadeInRemaining--;
            }

            MixStereoFrame(output, frame * channels, channels, sample, leftVolume, rightVolume);
            lastSample = sample;

            resamplePosition += sourceStep;
            while (resamplePosition >= 1.0)
            {
                resamplePosition -= 1.0;
                currentSample = nextSample;
                if (AvailableSamples == 0)
                {
                    Interlocked.Increment(ref underrunCount);
                    FadeOutStereo(
                        output,
                        (frame + 1) * channels,
                        channels,
                        outputSampleRate,
                        leftVolume,
                        rightVolume);
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

    private void FadeOut(float[] output, int offset, int channels, int outputSampleRate, float volume)
    {
        int remainingFrames = (output.Length - offset) / channels;
        int fadeFrames = Math.Min(Math.Max(1, outputSampleRate * FadeMilliseconds / 1000), remainingFrames);
        for (int frame = 0; frame < fadeFrames; frame++)
        {
            float gain = 1f - (frame + 1f) / fadeFrames;
            for (int channel = 0; channel < channels; channel++)
                output[offset + frame * channels + channel] += lastSample * gain * volume;
        }
        lastSample = 0f;
    }

    private void FadeOutStereo(
        float[] output,
        int offset,
        int channels,
        int outputSampleRate,
        float leftVolume,
        float rightVolume)
    {
        int remainingFrames = (output.Length - offset) / channels;
        int fadeFrames = Math.Min(Math.Max(1, outputSampleRate * FadeMilliseconds / 1000), remainingFrames);
        for (int frame = 0; frame < fadeFrames; frame++)
        {
            float gain = 1f - (frame + 1f) / fadeFrames;
            MixStereoFrame(
                output,
                offset + frame * channels,
                channels,
                lastSample * gain,
                leftVolume,
                rightVolume);
        }
        lastSample = 0f;
    }

    private static void MixStereoFrame(
        float[] output,
        int offset,
        int channels,
        float sample,
        float leftVolume,
        float rightVolume)
    {
        if (channels == 1)
        {
            output[offset] += sample * (leftVolume + rightVolume) * 0.5f;
            return;
        }

        output[offset] += sample * leftVolume;
        output[offset + 1] += sample * rightVolume;
        float centerVolume = (leftVolume + rightVolume) * 0.5f;
        for (int channel = 2; channel < channels; channel++)
            output[offset + channel] += sample * centerVolume;
    }
}
