using System;

namespace GuitarBridge;

internal static class OutputLevel
{
    public const int MaximumPercent = 500;
    public const float MaximumGain = MaximumPercent / 100f;
    public static float Normalize(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, MaximumGain) : 0;
}

// Stereo-linked peak limiting with instantaneous attack and a 50 ms release.
// It changes gain without delaying samples or adding an audio buffer.
internal sealed class OutputLimiter
{
    private readonly float release;
    private float attenuation = 1;
    public float ReductionDb => -20 * MathF.Log10(Math.Max(attenuation, 0.000001f));

    public OutputLimiter(int sampleRate) => release = (float)(1 - Math.Exp(-1.0 / (0.05 * sampleRate)));
    public void Reset() => attenuation = 1;

    public void Process(ref float left, ref float right)
    {
        if (!float.IsFinite(left)) left = 0;
        if (!float.IsFinite(right)) right = 0;
        float peak = Math.Max(Math.Abs(left), Math.Abs(right));
        float required = peak > 1 ? 1 / peak : 1;
        if (required < attenuation) attenuation = required;
        else attenuation = Math.Min(required, attenuation + (1 - attenuation) * release);
        left = Math.Clamp(left * attenuation, -1, 1);
        right = Math.Clamp(right * attenuation, -1, 1);
    }
}
