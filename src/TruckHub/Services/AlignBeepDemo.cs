using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TruckHub.Services;

/// <summary>
/// The Settings "Test" for the ALIGN beeps: a short made-up approach played through the REAL scheduler and sounds, so what you
/// hear is what the window will do - the beeps speeding up from 5 m with the pitch climbing from "off line" to "lined up" and
/// the sound moving from the left speaker to the middle, the steady tone at the pin, then the "too fast" and "past the hook"
/// sounds. About 7 seconds.
/// </summary>
public static class AlignBeepDemo
{
    public enum Action { Play, PlayLoop, StopLoop }

    public readonly record struct Step(double Seconds, Action Action, BeepSound Sound);

    private sealed class Recorder : IBeepOutput
    {
        public readonly List<Step> Steps = new();
        public double Now;

        public void Play(BeepSound sound) => Steps.Add(new Step(Now, Action.Play, sound));
        public void PlayLoop(BeepSound sound) => Steps.Add(new Step(Now, Action.PlayLoop, sound));
        public void StopLoop() => Steps.Add(new Step(Now, Action.StopLoop, default));
        public void SetVolume(int volumePercent) { }
    }

    /// <summary>The demo as a timed list of sounds, built by running the real <see cref="AlignBeeper"/> on a virtual clock.</summary>
    public static IReadOnlyList<Step> Build()
    {
        var recorder = new Recorder();
        var beeper = new AlignBeeper(recorder);
        beeper.Configure(enabled: true, volumePercent: 50);

        const double Frame = 1.0 / 30.0;
        const double ApproachEnd = 3.6, ToneEnd = 4.4, TooFastStart = 5.0, TooFastEnd = 6.2, PastStart = 6.5, End = 7.0;
        for (var t = 0.0; t < End; t += Frame)
        {
            recorder.Now = t;
            BeepCue cue;
            if (t < ApproachEnd)
            {
                var distance = 5.0 - 4.5 * t / ApproachEnd;
                var pitch = distance > 3.0 ? 2 : distance > 1.5 ? 1 : 0;
                var pan = distance >= 1.5 ? -Math.Min(1.0, (distance - 1.5) / 3.5) : 0.0;
                cue = new BeepCue(BeepKind.Proximity, AlignBeepPlanner.IntervalFor(distance), pitch, pan);
            }
            else if (t < ToneEnd)
            {
                cue = new BeepCue(BeepKind.Continuous, 0.0, 0, 0.0);
            }
            else if (t >= TooFastStart && t < TooFastEnd)
            {
                cue = new BeepCue(BeepKind.TooFast, AlignBeepPlanner.TooFastIntervalSeconds, 0, 0.0);
            }
            else if (t >= PastStart)
            {
                cue = new BeepCue(BeepKind.PastHook, AlignBeepPlanner.PastHookIntervalSeconds, 2, 0.0);
            }
            else
            {
                cue = BeepCue.None;
            }

            beeper.Update(t, cue, speedMps: 0.3);
        }

        recorder.Now = End;
        beeper.Silence();
        return recorder.Steps;
    }

    /// <summary>Plays the demo at this 0-100 volume without blocking the caller; 0 plays nothing.</summary>
    public static Task PlayAsync(int volumePercent)
    {
        if (volumePercent <= 0)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            using var output = new SoundPlayerBeepOutput();
            output.SetVolume(volumePercent);
            var steps = Build();
            var started = Environment.TickCount64;
            foreach (var step in steps)
            {
                var wait = (int)(step.Seconds * 1000 - (Environment.TickCount64 - started));
                if (wait > 0)
                {
                    await Task.Delay(wait);
                }

                switch (step.Action)
                {
                    case Action.Play: output.Play(step.Sound); break;
                    case Action.PlayLoop: output.PlayLoop(step.Sound); break;
                    default: output.StopLoop(); break;
                }
            }

            await Task.Delay(500);
        });
    }
}
