using System;
using System.Collections.Generic;
using System.IO;
using System.Media;

namespace TruckHub.Services;

/// <summary>
/// The real ALIGN beep output: one cached <see cref="SoundPlayer"/> per sound (kind x pitch x stereo position), rendered on first
/// use at the current volume and rebuilt only when the volume moves. Never throws - no sound device or a driver hiccup must
/// not take the ALIGN window down; the first failure is logged and the beeps are skipped.
/// </summary>
public sealed class SoundPlayerBeepOutput : IBeepOutput, IDisposable
{
    private readonly Dictionary<BeepSound, SoundPlayer> _players = new();
    private SoundPlayer? _loop;
    private int _volume = AlertSound.DefaultVolumePercent;
    private bool _failureLogged;

    public void SetVolume(int volumePercent)
    {
        var volume = Math.Clamp(volumePercent, 0, 100);
        if (volume == _volume)
        {
            return;
        }

        _volume = volume;
        StopLoop();
        DisposePlayers();
    }

    public void Play(BeepSound sound) => Try(() => PlayerFor(sound).Play());

    public void PlayLoop(BeepSound sound)
    {
        Try(() =>
        {
            _loop?.Stop();
            _loop = PlayerFor(sound);
            _loop.PlayLooping();
        });
    }

    public void StopLoop() => Try(() =>
    {
        _loop?.Stop();
        _loop = null;
    });

    public void Dispose()
    {
        StopLoop();
        DisposePlayers();
    }

    private SoundPlayer PlayerFor(BeepSound sound)
    {
        if (!_players.TryGetValue(sound, out var player))
        {
            player = new SoundPlayer(new MemoryStream(AlignBeepSynth.RenderWav(sound, _volume)));
            player.Load();
            _players[sound] = player;
        }

        return player;
    }

    private void DisposePlayers()
    {
        foreach (var player in _players.Values)
        {
            player.Dispose();
        }

        _players.Clear();
    }

    private void Try(Action action)
    {
        if (_volume == 0)
        {
            return;
        }

        try
        {
            action();
        }
        catch (Exception ex)
        {
            if (!_failureLogged)
            {
                _failureLogged = true;
                AppLogger.Log($"AlignBeeps: could not play an ALIGN beep - {ex.Message}");
            }
        }
    }
}
