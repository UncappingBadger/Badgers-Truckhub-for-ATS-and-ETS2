using System;
using System.IO;

namespace TruckHub.Services;

/// <summary>Which pre-rendered sound: the kind, the pitch zone (0-2) and the stereo position step (-2 .. +2).</summary>
public readonly record struct BeepSound(BeepKind Kind, int Pitch, int PanIndex);

/// <summary>
/// The ALIGN beep sounds, synthesized in memory like <see cref="AlertSound"/> (no sound files to ship): plain sine tones,
/// stereo so the beep can sit toward the side the kingpin is on. Five stereo positions are rendered (hard left, left,
/// centre, right, hard right) with constant-power gains, and the volume is baked into the samples the same way the speed
/// alerts do it, so the Settings slider means the same thing for both.
///
/// The pitches stay clear of the speed alerts' 0.7 / 1.0 / 1.2 kHz and of the game's own reverse beeper:
/// lined up 2.0 kHz, almost 1.65 kHz, off line 1.35 kHz.
/// </summary>
public static class AlignBeepSynth
{
    public const int SampleRate = 22050;
    public const int PanSteps = 5;                       // -2 .. +2
    public const int ProximityBeepMs = 60;
    public const double LoopSeconds = 0.4;               // an exact number of cycles at each pitch below, so the loop has no click

    private const int FadeMs = 6;

    public static readonly double[] PitchHz = { 2000.0, 1650.0, 1350.0 };
    public const double PastHookHz = 900.0;
    public const int PastHookBeepMs = 55;
    public const int TooFastToneMs = 70;

    /// <summary>-1..+1 for a pan index -2..+2.</summary>
    public static double PanValue(int panIndex) => Math.Clamp(panIndex, -2, 2) / 2.0;

    /// <summary>The nearest of the five stereo steps for a pan of -1..+1.</summary>
    public static int PanIndexFor(double pan) => (int)Math.Round(Math.Clamp(pan, -1.0, 1.0) * 2.0, MidpointRounding.AwayFromZero);

    /// <summary>Constant-power left/right gains for a pan of -1 (left) .. +1 (right): hard left is (1, 0), centre (0.707, 0.707).</summary>
    public static (double Left, double Right) Gains(double pan)
    {
        var angle = (Math.Clamp(pan, -1.0, 1.0) + 1.0) * Math.PI / 4.0;
        return (Math.Cos(angle), Math.Sin(angle));
    }

    /// <summary>The sound as interleaved stereo 16-bit samples; a continuous tone is one loopable cycle block.</summary>
    public static short[] Render(BeepSound sound, int volumePercent)
    {
        var peak = AlertSound.PeakForVolume(volumePercent);
        var (gl, gr) = Gains(PanValue(sound.PanIndex));
        var pitch = Math.Clamp(sound.Pitch, 0, PitchHz.Length - 1);

        switch (sound.Kind)
        {
            case BeepKind.Continuous:
                return Mix(Tone(PitchHz[pitch], (int)(SampleRate * LoopSeconds), peak, fade: false), gl, gr);
            case BeepKind.TooFast:
                // a low tone then a high tone: unlike any other cue, and not the speed alert's same-pitch double beep
                return Mix(Concat(Tone(PitchHz[2], SampleRate * TooFastToneMs / 1000, peak), Silence(SampleRate * 45 / 1000),
                    Tone(PitchHz[0], SampleRate * TooFastToneMs / 1000, peak)), gl, gr);
            case BeepKind.PastHook:
                return Mix(Tone(PastHookHz, SampleRate * PastHookBeepMs / 1000, peak), gl, gr);
            default:
                return Mix(Tone(PitchHz[pitch], SampleRate * ProximityBeepMs / 1000, peak), gl, gr);
        }
    }

    /// <summary>A standard 16-bit stereo PCM WAV file around <paramref name="interleaved"/>.</summary>
    public static byte[] ToWav(short[] interleaved)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream))
        {
            var dataBytes = interleaved.Length * 2;
            w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
            w.Write(36 + dataBytes);
            w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
            w.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
            w.Write(16);
            w.Write((short)1);                      // PCM
            w.Write((short)2);                      // stereo
            w.Write(SampleRate);
            w.Write(SampleRate * 4);                // byte rate
            w.Write((short)4);                      // block align
            w.Write((short)16);
            w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
            w.Write(dataBytes);
            foreach (var s in interleaved)
            {
                w.Write(s);
            }
        }

        return stream.ToArray();
    }

    public static byte[] RenderWav(BeepSound sound, int volumePercent) => ToWav(Render(sound, volumePercent));

    // ------------------------------------------------------------------ building blocks (mono, as doubles)

    private static double[] Tone(double hz, int samples, double peak, bool fade = true)
    {
        var data = new double[samples];
        var fadeSamples = SampleRate * FadeMs / 1000;
        for (var i = 0; i < samples; i++)
        {
            var envelope = 1.0;
            if (fade)
            {
                if (i < fadeSamples) envelope = (double)i / fadeSamples;
                else if (i >= samples - fadeSamples) envelope = (double)(samples - 1 - i) / fadeSamples;
            }

            data[i] = Math.Sin(2.0 * Math.PI * hz * i / SampleRate) * peak * envelope;
        }

        return data;
    }

    private static double[] Silence(int samples) => new double[samples];

    private static double[] Concat(params double[][] parts)
    {
        var total = 0;
        foreach (var p in parts) total += p.Length;
        var all = new double[total];
        var at = 0;
        foreach (var p in parts)
        {
            Array.Copy(p, 0, all, at, p.Length);
            at += p.Length;
        }

        return all;
    }

    private static short[] Mix(double[] mono, double leftGain, double rightGain)
    {
        var stereo = new short[mono.Length * 2];
        for (var i = 0; i < mono.Length; i++)
        {
            stereo[2 * i] = (short)Math.Round(Math.Clamp(mono[i] * leftGain, -1.0, 1.0) * short.MaxValue);
            stereo[2 * i + 1] = (short)Math.Round(Math.Clamp(mono[i] * rightGain, -1.0, 1.0) * short.MaxValue);
        }

        return stereo;
    }
}
