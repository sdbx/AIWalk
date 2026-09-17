using System;
using UnityEngine;
using UnityEngine.InputSystem;

[AddComponentMenu("Debug/Voice Echo Reference Debug View")]
[RequireComponent(typeof(PlayerVoiceChat))]
public sealed class VoiceEchoReferenceDebugView : MonoBehaviour
{
    private const int SampleRate = 48000;
    private const int MaximumHistorySamples = SampleRate * 3;

    [SerializeField] private PlayerVoiceChat voiceChat;
    [SerializeField, Range(0.25f, 3f)] private float visibleSeconds = 1f;
    [SerializeField] private Key toggleKey = Key.F8;
    [SerializeField] private bool visible = true;

    private readonly SignalHistory unityReference = new(MaximumHistorySamples);
    private readonly SignalHistory systemReference = new(MaximumHistorySamples);
    private readonly SignalHistory microphone = new(MaximumHistorySamples);
    private readonly SignalHistory aecOutput = new(MaximumHistorySamples);

    private Texture2D pixel;
    private GUIStyle labelStyle;
    private float unityRms;
    private float systemRms;
    private float microphoneRms;
    private float outputRms;
    private float unityToSystemDelayMs;
    private float unityToSystemCorrelation;
    private float systemToMicrophoneDelayMs;
    private float systemToMicrophoneCorrelation;
    private float nextAnalysisTime;

    private void Awake()
    {
        if (voiceChat == null)
            voiceChat = GetComponent<PlayerVoiceChat>();

        pixel = new Texture2D(1, 1, TextureFormat.RGBA32, false)
        {
            name = "Voice Echo Debug Pixel",
            hideFlags = HideFlags.HideAndDontSave
        };
        pixel.SetPixel(0, 0, Color.white);
        pixel.Apply();
    }

    private void OnEnable()
    {
        if (voiceChat == null)
            voiceChat = GetComponent<PlayerVoiceChat>();
        if (voiceChat != null)
            voiceChat.EchoDebugFrameProcessed += OnEchoDebugFrame;
    }

    private void OnDisable()
    {
        if (voiceChat != null)
            voiceChat.EchoDebugFrameProcessed -= OnEchoDebugFrame;
    }

    private void Update()
    {
        if (Keyboard.current != null && Keyboard.current[toggleKey].wasPressedThisFrame)
            visible = !visible;

        if (Time.unscaledTime < nextAnalysisTime)
            return;

        nextAnalysisTime = Time.unscaledTime + 0.25f;
        AnalyzeDelay(unityReference, systemReference,
            out unityToSystemDelayMs, out unityToSystemCorrelation);
        AnalyzeDelay(systemReference, microphone,
            out systemToMicrophoneDelayMs, out systemToMicrophoneCorrelation);
    }

    private void OnEchoDebugFrame(
        float[] unity,
        float[] system,
        float[] mic,
        float[] processed)
    {
        unityReference.Write(unity);
        systemReference.Write(system);
        microphone.Write(mic);
        aecOutput.Write(processed);

        unityRms = CalculateRms(unity);
        systemRms = CalculateRms(system);
        microphoneRms = CalculateRms(mic);
        outputRms = CalculateRms(processed);
    }

    private void OnGUI()
    {
        if (!visible || pixel == null || Event.current.type != EventType.Repaint)
            return;

        labelStyle ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            normal = { textColor = Color.white }
        };

        float width = Mathf.Min(Screen.width - 24f, 920f);
        float panelHeight = Mathf.Min(Screen.height - 24f, 620f);
        var panel = new Rect(12f, 12f, width, panelHeight);
        GUI.Box(panel, GUIContent.none);

        GUI.Label(new Rect(panel.x + 12f, panel.y + 8f, panel.width - 24f, 22f),
            $"AEC reference comparison — {visibleSeconds:0.00}s window — {toggleKey} to hide", labelStyle);
        GUI.Label(new Rect(panel.x + 12f, panel.y + 28f, panel.width - 24f, 22f),
            $"Unity → WASAPI: {FormatDelay(unityToSystemDelayMs, unityToSystemCorrelation)}    " +
            $"WASAPI → Mic: {FormatDelay(systemToMicrophoneDelayMs, systemToMicrophoneCorrelation)}",
            labelStyle);

        float graphTop = panel.y + 56f;
        float graphHeight = (panel.height - 68f) / 4f;
        int sampleCount = Mathf.Clamp(
            Mathf.RoundToInt(visibleSeconds * SampleRate), 1, MaximumHistorySamples);
        float sharedPeak = Mathf.Max(
            0.01f,
            unityReference.Peak(sampleCount),
            systemReference.Peak(sampleCount),
            microphone.Peak(sampleCount),
            aecOutput.Peak(sampleCount));

        DrawSignal(unityReference, new Rect(panel.x + 8f, graphTop, panel.width - 16f, graphHeight),
            sampleCount, sharedPeak, new Color(0.25f, 0.75f, 1f),
            $"Unity voice reference  RMS {ToDb(unityRms):0.0} dBFS");
        DrawSignal(systemReference, new Rect(panel.x + 8f, graphTop + graphHeight, panel.width - 16f, graphHeight),
            sampleCount, sharedPeak, new Color(1f, 0.75f, 0.2f),
            $"WASAPI actual output  RMS {ToDb(systemRms):0.0} dBFS");
        DrawSignal(microphone, new Rect(panel.x + 8f, graphTop + graphHeight * 2f, panel.width - 16f, graphHeight),
            sampleCount, sharedPeak, new Color(0.35f, 1f, 0.5f),
            $"Microphone input  RMS {ToDb(microphoneRms):0.0} dBFS");
        DrawSignal(aecOutput, new Rect(panel.x + 8f, graphTop + graphHeight * 3f, panel.width - 16f, graphHeight),
            sampleCount, sharedPeak, new Color(1f, 0.4f, 0.65f),
            $"AEC3 output  RMS {ToDb(outputRms):0.0} dBFS");
    }

    private void DrawSignal(
        SignalHistory signal,
        Rect area,
        int sampleCount,
        float sharedPeak,
        Color color,
        string label)
    {
        GUI.Label(new Rect(area.x + 4f, area.y, area.width - 8f, 20f), label, labelStyle);

        var graph = new Rect(area.x + 4f, area.y + 20f, area.width - 8f, area.height - 24f);
        Color oldColor = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.08f);
        GUI.DrawTexture(graph, pixel);
        GUI.color = new Color(1f, 1f, 1f, 0.25f);
        GUI.DrawTexture(new Rect(graph.x, graph.center.y, graph.width, 1f), pixel);

        GUI.color = color;
        int columns = Mathf.Max(1, Mathf.FloorToInt(graph.width));
        int available = Mathf.Min(sampleCount, signal.Count);
        if (available > 0)
        {
            for (int column = 0; column < columns; column++)
            {
                int from = available * column / columns;
                int to = Mathf.Max(from + 1, available * (column + 1) / columns);
                signal.GetMinMaxFromLatest(available, from, to, out float minimum, out float maximum);

                float top = graph.center.y - maximum / sharedPeak * graph.height * 0.48f;
                float bottom = graph.center.y - minimum / sharedPeak * graph.height * 0.48f;
                GUI.DrawTexture(new Rect(graph.x + column, top, 1f, Mathf.Max(1f, bottom - top)), pixel);
            }
        }
        GUI.color = oldColor;
    }

    private static void AnalyzeDelay(
        SignalHistory first,
        SignalHistory second,
        out float delayMilliseconds,
        out float correlation)
    {
        const int downsample = 24;
        const int analysisSamples = SampleRate / 2;
        const int maximumDelaySamples = SampleRate * 300 / 1000;
        const int requiredSamples = analysisSamples + maximumDelaySamples * 2;

        delayMilliseconds = 0f;
        correlation = 0f;
        if (first.Count < requiredSamples || second.Count < requiredSamples)
            return;

        float bestCorrelation = 0f;
        int bestDelay = 0;
        for (int delay = -maximumDelaySamples; delay <= maximumDelaySamples; delay += downsample)
        {
            double product = 0;
            double firstPower = 0;
            double secondPower = 0;
            for (int i = 0; i < analysisSamples; i += downsample)
            {
                float a = first.GetFromLatest(requiredSamples, i + maximumDelaySamples);
                float b = second.GetFromLatest(
                    requiredSamples,
                    i + maximumDelaySamples + delay);
                product += a * b;
                firstPower += a * a;
                secondPower += b * b;
            }

            double denominator = Math.Sqrt(firstPower * secondPower);
            float current = denominator > 0.000000001
                ? (float)(product / denominator)
                : 0f;
            if (current > bestCorrelation)
            {
                bestCorrelation = current;
                bestDelay = delay;
            }
        }

        correlation = bestCorrelation;
        delayMilliseconds = bestDelay * 1000f / SampleRate;
    }

    private static float CalculateRms(float[] samples)
    {
        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
            sum += samples[i] * samples[i];
        return Mathf.Sqrt((float)(sum / samples.Length));
    }

    private static float ToDb(float value)
    {
        return 20f * Mathf.Log10(Mathf.Max(value, 0.000001f));
    }

    private static string FormatDelay(float delayMs, float correlation)
    {
        return correlation < 0.15f
            ? "no reliable match"
            : $"{delayMs:+0.0;-0.0;0.0} ms, correlation {correlation:0.00}";
    }

    private void OnDestroy()
    {
        if (pixel != null)
            Destroy(pixel);
    }

    private sealed class SignalHistory
    {
        private readonly float[] samples;
        private int writePosition;

        public int Count { get; private set; }

        public SignalHistory(int capacity)
        {
            samples = new float[capacity];
        }

        public void Write(float[] input)
        {
            for (int i = 0; i < input.Length; i++)
            {
                samples[writePosition] = input[i];
                writePosition = (writePosition + 1) % samples.Length;
                Count = Mathf.Min(Count + 1, samples.Length);
            }
        }

        public float Peak(int requestedSamples)
        {
            int count = Mathf.Min(requestedSamples, Count);
            float peak = 0f;
            for (int i = 0; i < count; i++)
                peak = Mathf.Max(peak, Mathf.Abs(GetFromLatest(count, i)));
            return peak;
        }

        public float GetFromLatest(int windowSamples, int index)
        {
            int count = Mathf.Min(windowSamples, Count);
            if (index < 0 || index >= count)
                return 0f;

            int oldest = writePosition - count;
            if (oldest < 0)
                oldest += samples.Length;
            return samples[(oldest + index) % samples.Length];
        }

        public void GetMinMaxFromLatest(
            int windowSamples,
            int from,
            int to,
            out float minimum,
            out float maximum)
        {
            minimum = 0f;
            maximum = 0f;
            for (int i = from; i < to; i++)
            {
                float sample = GetFromLatest(windowSamples, i);
                minimum = Mathf.Min(minimum, sample);
                maximum = Mathf.Max(maximum, sample);
            }
        }
    }
}
