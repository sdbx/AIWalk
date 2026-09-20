using UnityEngine;

public static class VoiceDistanceAttenuation
{
    public static float Calculate(
        Vector3 sourcePosition,
        Vector3 listenerPosition,
        float minimumDistance,
        float maximumDistance,
        float falloffExponent)
    {
        return Calculate(
            Vector3.Distance(sourcePosition, listenerPosition),
            minimumDistance,
            maximumDistance,
            falloffExponent);
    }

    public static float Calculate(
        float distance,
        float minimumDistance,
        float maximumDistance,
        float falloffExponent)
    {
        minimumDistance = Mathf.Max(0f, minimumDistance);
        maximumDistance = Mathf.Max(minimumDistance + 0.0001f, maximumDistance);
        falloffExponent = Mathf.Max(0.01f, falloffExponent);

        if (distance <= minimumDistance)
            return 1f;
        if (distance >= maximumDistance)
            return 0f;

        float normalizedDistance = Mathf.InverseLerp(
            minimumDistance,
            maximumDistance,
            distance);
        return Mathf.Pow(1f - normalizedDistance, falloffExponent);
    }
}
