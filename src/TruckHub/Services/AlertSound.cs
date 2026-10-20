using System;
using System.IO;
using System.Media;

namespace TruckHub.Services;

/// <summary>
/// The speed-alert sounds, synthesized in memory so there is no sound asset to ship, license or keep
/// in step with the embedded-resource list. Deliberately plain sine beeps that cut through engine and
/// game audio without startling you, told apart by shape rather than loudness:
///   - over-limit WARNING: a double beep at 1.2 kHz
///   - speed limit went UP: one beep, higher tone (1.0 kHz)
///   - speed limit went DOWN: one beep, lower tone (0.7 kHz)
/// Loudness is the user's choice (one Settings slider, 0-100%, shared by all three).
/// </summary>
public static class AlertSound
{
    /// <summary>Slider default. 50% is the original, comfortable level (peak ~30% of full scale).</summary>
    public const int DefaultVolumePercent = 50;

    private const int SampleRate = 22050;
    private const int BeepMs = 80;
    private const int GapMs = 70;
    private const int FadeMs = 6;        // short linear fade in/out so each burst starts and ends without a click

    public const double WarningToneHz = 1200.0;
    public const double LimitUpToneHz = 1000.0;
    public const double LimitDownToneHz = 700.0;

    // Peak amplitude (fraction of full scale) AT the default 50% setting. Volume scales this linearly:
    // 100% = twice that (peak ~0.60, loud but nowhere near clipping), 0% = silent.
    private const double PeakAtDefaultVolume = 0.30;

    private sealed class Cue
    {
        public SoundPlayer? Player;
        public int Volume = -1;
    }

    // One cached player per sound, rebuilt only when the slider actually moved.
    private static readonly Cue Warning = new();
    private static readonly Cue LimitUp = new();
    private static readonly Cue LimitDown = new();
    private static bool _failureLogged;

    /// <summary>Peak amplitude (0..~0.6 of full scale) for a 0-100 volume setting.</summary>
    public static double PeakForVolume(int volumePercent) =>
        PeakAtDefaultVolume * Math.Clamp(volumePercent, 0, 100) / DefaultVolumePercent;

    /// <summary>The over-limit warning: two beeps. Public so the file can be verified without playing it.</summary>
    public static byte[] BuildDoubleBeepWav(int volumePercent = DefaultVolumePercent) =>
        BuildBeepsWav(volumePercent, WarningToneHz, beepCount: 2);

    /// <summary>The limit-change cue: ONE beep, higher when the limit went up, lower when it went down.</summary>
    public static byte[] BuildLimitChangeBeepWav(bool increased, int volumePercent = DefaultVolumePercent) =>
        BuildBeepsWav(volumePercent, increased ? LimitUpToneHz : LimitDownToneHz, beepCount: 1);

    private static byte[] BuildBeepsWav(int volumePercent, double toneHz, int beepCount)
    {
        var peak = PeakForVolume(volumePercent);
        var beepSamples = SampleRate * BeepMs / 1000;
        var gapSamples = SampleRate * GapMs / 1000;
        var fadeSamples = SampleRate * FadeMs / 1000;
        var total = beepSamples * beepCount + gapSamples * (beepCount - 1);
        var pcm = new short[total];

        for (var b = 0; b < beepCount; b++)
        {
            var start = b * (beepSamples + gapSamples);
            for (var i = 0; i < beepSamples; i++)
            {
                var envelope = 1.0;
                if (i < fadeSamples) envelope = (double)i / fadeSamples;
                else if (i >= beepSamples - fadeSamples) envelope = (double)(beepSamples - 1 - i) / fadeSamples;

                var sample = Math.Sin(2.0 * Math.PI * toneHz * i / SampleRate) * peak * envelope;
                pcm[start + i] = (short)Math.Round(sample * short.MaxValue);
            }
        }

        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream))
        {
            var dataBytes = pcm.Length * 2;
            w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
            w.Write(36 + dataBytes);
            w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
            w.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
            w.Write(16);                     // PCM fmt chunk size
            w.Write((short)1);               // PCM
            w.Write((short)1);               // mono
            w.Write(SampleRate);
            w.Write(SampleRate * 2);         // byte rate
            w.Write((short)2);               // block align
            w.Write((short)16);              // bits per sample
            w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
            w.Write(dataBytes);
            foreach (var s in pcm)
            {
                w.Write(s);
            }
        }

        return stream.ToArray();
    }

    /// <summary>Plays the over-limit double beep at the given 0-100 volume without blocking; 0 plays nothing.</summary>
    public static void PlayDoubleBeep(int volumePercent = DefaultVolumePercent) =>
        Play(Warning, volumePercent, v => BuildDoubleBeepWav(v));

    /// <summary>Plays the single limit-change beep (higher = limit went up) without blocking; 0 plays nothing.</summary>
    public static void PlayLimitChangeBeep(bool increased, int volumePercent = DefaultVolumePercent) =>
        Play(increased ? LimitUp : LimitDown, volumePercent, v => BuildLimitChangeBeepWav(increased, v));

    /// <summary>
    /// Never throws - no audio device or a driver hiccup must not take the HUD down; the failure is
    /// logged once and the cue is simply skipped.
    /// </summary>
    private static void Play(Cue cue, int volumePercent, Func<int, byte[]> build)
    {
        var volume = Math.Clamp(volumePercent, 0, 100);
        if (volume == 0)
        {
            return;
        }

        try
        {
            if (cue.Player is null || cue.Volume != volume)
            {
                var player = new SoundPlayer(new MemoryStream(build(volume)));
                player.Load();
                cue.Player?.Dispose();
                cue.Player = player;
                cue.Volume = volume;
            }

            cue.Player.Play();
        }
        catch (Exception ex)
        {
            if (!_failureLogged)
            {
                _failureLogged = true;
                AppLogger.Log($"AlertSound: could not play a speed alert sound - {ex.Message}");
            }
        }
    }
}
