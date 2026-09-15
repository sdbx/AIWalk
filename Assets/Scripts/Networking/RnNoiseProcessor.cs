using System;
using System.Runtime.InteropServices;
using UnityEngine;

internal sealed class RnNoiseProcessor : IDisposable
{
    public const int InputSampleRate = 48000;
    public const int OutputSampleRate = 16000;
    public const int InputFrameSamples = 480;
    public const int OutputFrameSamples = 160;

    private IntPtr state;
    private readonly float[] nativeInput = new float[InputFrameSamples];
    private readonly float[] nativeOutput = new float[InputFrameSamples];
    private readonly Downsampler downsampler = new();

    public RnNoiseProcessor()
    {
        if (NativeMethods.rnnoise_get_frame_size() != InputFrameSamples)
            throw new InvalidOperationException("The RNNoise plugin uses an unexpected frame size.");

        state = NativeMethods.rnnoise_create(IntPtr.Zero);
        if (state == IntPtr.Zero)
            throw new InvalidOperationException("RNNoise state could not be created.");
    }

    public float Process(float[] input, float[] output)
    {
        if (input == null || input.Length != InputFrameSamples)
            throw new ArgumentException($"RNNoise input must contain {InputFrameSamples} samples.", nameof(input));
        if (output == null || output.Length != OutputFrameSamples)
            throw new ArgumentException($"RNNoise output must contain {OutputFrameSamples} samples.", nameof(output));
        if (state == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(RnNoiseProcessor));

        for (int i = 0; i < input.Length; i++)
            nativeInput[i] = Mathf.Clamp(input[i], -1f, 1f) * 32768f;

        float voiceProbability = NativeMethods.rnnoise_process_frame(state, nativeOutput, nativeInput);
        downsampler.Process(nativeOutput, output);
        return voiceProbability;
    }

    public void Dispose()
    {
        if (state == IntPtr.Zero)
            return;

        NativeMethods.rnnoise_destroy(state);
        state = IntPtr.Zero;
    }

    private static class NativeMethods
    {
        [DllImport("rnnoise", CallingConvention = CallingConvention.Cdecl)]
        public static extern int rnnoise_get_frame_size();

        [DllImport("rnnoise", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr rnnoise_create(IntPtr model);

        [DllImport("rnnoise", CallingConvention = CallingConvention.Cdecl)]
        public static extern void rnnoise_destroy(IntPtr state);

        [DllImport("rnnoise", CallingConvention = CallingConvention.Cdecl)]
        public static extern float rnnoise_process_frame(
            IntPtr state,
            [Out] float[] output,
            [In] float[] input);
    }

    private sealed class Downsampler
    {
        private const int FilterLength = 48;
        private const int HistoryLength = FilterLength - 1;
        private const int Decimation = InputSampleRate / OutputSampleRate;
        private readonly float[] coefficients = CreateLowPassFilter();
        private readonly float[] history = new float[HistoryLength];
        private readonly float[] combined = new float[HistoryLength + InputFrameSamples];

        public void Process(float[] input, float[] output)
        {
            Array.Copy(history, 0, combined, 0, HistoryLength);
            Array.Copy(input, 0, combined, HistoryLength, InputFrameSamples);

            for (int outputIndex = 0; outputIndex < OutputFrameSamples; outputIndex++)
            {
                int inputIndex = HistoryLength + outputIndex * Decimation;
                double sample = 0;
                for (int tap = 0; tap < FilterLength; tap++)
                    sample += coefficients[tap] * combined[inputIndex - tap];
                output[outputIndex] = Mathf.Clamp((float)(sample / 32768.0), -1f, 1f);
            }

            Array.Copy(combined, combined.Length - HistoryLength, history, 0, HistoryLength);
        }

        private static float[] CreateLowPassFilter()
        {
            const double cutoff = 0.15; // 7.2 kHz at 48 kHz; below the 16 kHz output Nyquist limit.
            var filter = new float[FilterLength];
            double center = (FilterLength - 1) * 0.5;
            double sum = 0;

            for (int i = 0; i < FilterLength; i++)
            {
                double distance = i - center;
                double sinc = Math.Abs(distance) < 0.000001
                    ? 2.0 * cutoff
                    : Math.Sin(2.0 * Math.PI * cutoff * distance) / (Math.PI * distance);
                double window = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / (FilterLength - 1));
                filter[i] = (float)(sinc * window);
                sum += filter[i];
            }

            for (int i = 0; i < filter.Length; i++)
                filter[i] = (float)(filter[i] / sum);
            return filter;
        }
    }
}
