using System;
using System.Runtime.InteropServices;

internal sealed class WebRtcAecProcessor : IDisposable
{
    public const int SampleRate = 48000;
    public const int FrameSamples = 480;

    private IntPtr state;

    public WebRtcAecProcessor(int? streamDelayMilliseconds = null)
    {
        state = NativeMethods.unity_aec3_create();
        if (state == IntPtr.Zero)
            throw new InvalidOperationException("WebRTC AEC3 state could not be created.");

        // Without an external delay AEC3 continuously estimates the render-to-capture
        // delay itself. A fixed value is kept only as a fallback for unusual devices.
        if (streamDelayMilliseconds.HasValue)
            NativeMethods.unity_aec3_set_delay_ms(state, Math.Max(0, streamDelayMilliseconds.Value));
    }

    public void Process(float[] render, float[] capture, float[] output)
    {
        ValidateFrame(render, nameof(render));
        ValidateFrame(capture, nameof(capture));
        ValidateFrame(output, nameof(output));
        if (state == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(WebRtcAecProcessor));

        int result = NativeMethods.unity_aec3_process(state, render, capture, output);
        if (result != 0)
            throw new InvalidOperationException($"WebRTC AEC3 processing failed with code {result}.");
    }

    public void Dispose()
    {
        if (state == IntPtr.Zero)
            return;

        NativeMethods.unity_aec3_destroy(state);
        state = IntPtr.Zero;
    }

    private static void ValidateFrame(float[] frame, string parameterName)
    {
        if (frame == null || frame.Length != FrameSamples)
            throw new ArgumentException($"AEC3 frames must contain {FrameSamples} samples.", parameterName);
    }

    private static class NativeMethods
    {
        [DllImport("webrtc_aec3", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr unity_aec3_create();

        [DllImport("webrtc_aec3", CallingConvention = CallingConvention.Cdecl)]
        public static extern void unity_aec3_destroy(IntPtr state);

        [DllImport("webrtc_aec3", CallingConvention = CallingConvention.Cdecl)]
        public static extern int unity_aec3_process(
            IntPtr state,
            [In] float[] render,
            [In] float[] capture,
            [Out] float[] output);

        [DllImport("webrtc_aec3", CallingConvention = CallingConvention.Cdecl)]
        public static extern void unity_aec3_set_delay_ms(IntPtr state, int delayMilliseconds);
    }
}
