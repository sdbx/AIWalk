using System;
using System.Runtime.InteropServices;

internal sealed class WasapiLoopbackCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int FrameSamples = 480;

    private IntPtr state;

    public int BufferedSamples => state == IntPtr.Zero
        ? 0
        : NativeMethods.unity_wasapi_loopback_buffered_samples(state);

    public int StreamLatencyMilliseconds => state == IntPtr.Zero
        ? 0
        : NativeMethods.unity_wasapi_loopback_latency_ms(state);

    public WasapiLoopbackCapture()
    {
        state = NativeMethods.unity_wasapi_loopback_create();
        if (state == IntPtr.Zero)
            throw new InvalidOperationException("WASAPI loopback capture could not be started.");
    }

    public bool ReadFrame(float[] output)
    {
        if (output == null || output.Length != FrameSamples)
            throw new ArgumentException($"Loopback frames must contain {FrameSamples} samples.", nameof(output));
        if (state == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(WasapiLoopbackCapture));

        int read = NativeMethods.unity_wasapi_loopback_read(state, output, output.Length);
        if (read < 0)
            throw new InvalidOperationException($"WASAPI loopback read failed with code {read}.");
        if (read == output.Length)
            return true;

        Array.Clear(output, 0, output.Length);
        return false;
    }

    public void Dispose()
    {
        if (state == IntPtr.Zero)
            return;

        NativeMethods.unity_wasapi_loopback_destroy(state);
        state = IntPtr.Zero;
    }

    private static class NativeMethods
    {
        [DllImport("wasapi_loopback", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr unity_wasapi_loopback_create();

        [DllImport("wasapi_loopback", CallingConvention = CallingConvention.Cdecl)]
        public static extern void unity_wasapi_loopback_destroy(IntPtr state);

        [DllImport("wasapi_loopback", CallingConvention = CallingConvention.Cdecl)]
        public static extern int unity_wasapi_loopback_read(
            IntPtr state,
            [Out] float[] output,
            int sampleCount);

        [DllImport("wasapi_loopback", CallingConvention = CallingConvention.Cdecl)]
        public static extern int unity_wasapi_loopback_buffered_samples(IntPtr state);

        [DllImport("wasapi_loopback", CallingConvention = CallingConvention.Cdecl)]
        public static extern int unity_wasapi_loopback_latency_ms(IntPtr state);
    }
}

