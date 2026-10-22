using System;

namespace TruckHub.Services;

/// <summary>The words and colours the ALIGN panel shows for one <see cref="HitchFrame"/>.</summary>
public sealed record AlignReadout
{
    public string StatusText { get; init; } = "";
    public string StatusColor { get; init; } = AlignPresenter.Neutral;

    public string SideText { get; init; } = "--";
    public string SideColor { get; init; } = AlignPresenter.Dim;
    public string GapText { get; init; } = "--";
    public string GapColor { get; init; } = AlignPresenter.Dim;
    public string HeightText { get; init; } = "--";
    public string HeightColor { get; init; } = AlignPresenter.Dim;
    public string AngleText { get; init; } = "--";
    public string AngleColor { get; init; } = AlignPresenter.Dim;

    /// <summary>True when the picture has nothing to show and the status text should sit over it instead.</summary>
    public bool OverlayMessage { get; init; }
}

/// <summary>
/// Turns a <see cref="HitchFrame"/> into the ALIGN panel's text. Pure and UI-free so the wording and the unit handling
/// can be checked without a window. Lengths follow the user's speed/distance unit: centimetres and metres, or inches
/// and feet.
/// </summary>
public static class AlignPresenter
{
    public const string Green = "#5FD85F";
    public const string Amber = "#FFC24C";
    public const string Red = "#FF4C4C";
    public const string White = "#FFFFFF";
    public const string Neutral = "#DDDDDD";
    public const string Dim = "#666666";

    /// <summary>The standard "nothing connected yet" line the main window and EXT already use.</summary>
    public const string WaitingForGame = "Waiting for ETS2 / ATS to start…";

    private const double MetresToInches = 39.3700787;
    private const double MetresToFeet = 3.2808399;

    public static AlignReadout Present(HitchFrame f, bool metric)
    {
        switch (f.State)
        {
            case HitchState.NoData:
                // Same wording as the main window and EXT. The view shows this on its own, with no picture or readouts, until the sim is connected.
                return new AlignReadout { StatusText = WaitingForGame, StatusColor = Dim, OverlayMessage = true };
            case HitchState.NeedsPlugin:
                return new AlignReadout { StatusText = "UPDATE THE TELEMETRY PLUGIN", StatusColor = Amber, OverlayMessage = true };
            case HitchState.NoTrailer:
                return new AlignReadout { StatusText = "NO TRAILER TO HOOK UP", StatusColor = Dim, OverlayMessage = true };
        }

        var side = Zone(f.SideZone);
        var height = Zone(f.HeightZone);

        if (f.State == HitchState.Coupled)
        {
            return new AlignReadout
            {
                StatusText = $"HOOKED UP  -  trailer angle {Math.Abs(f.YawDeg):0} deg",
                StatusColor = Green,
                SideText = "--", GapText = "--", HeightText = "--",
                AngleText = $"{Math.Abs(f.YawDeg):0}°", AngleColor = Neutral,
            };
        }

        var angle = $"{Math.Abs(f.YawDeg):0}°";

        if (!f.InRearView)
        {
            // Beside, ahead of, or far from the truck: the rear picture cannot show it, so say where it is.
            return new AlignReadout
            {
                StatusText = f.DistanceM > 80 ? $"TRAILER {Length(f.DistanceM, metric, far: true)} AWAY" : $"TRAILER {Length(f.DistanceM, metric, far: true)}  -  {Bearing(f)}",
                StatusColor = Neutral,
                GapText = Length(f.DistanceM, metric, far: true), GapColor = Neutral,
                AngleText = angle, AngleColor = Dim,
            };
        }

        var sideText = SideText(f.LeftM, metric);
        var heightText = HeightText(f.UpM, metric);
        var gapText = f.BehindM < 0 ? $"PAST {Length(-f.BehindM, metric)}" : Length(f.BehindM, metric);

        string status, color;
        if (f.BehindM < HitchTolerance.OvershootM)
        {
            status = "PAST THE HOOK  -  PULL FORWARD";
            color = Red;
        }
        else if (f.LinedUp)
        {
            status = f.BehindM <= HitchTolerance.HookGapM ? "READY  -  HOOK UP" : "LINED UP  -  KEEP REVERSING";
            color = Green;
        }
        else if (f.SideZone != HitchZone.Green)
        {
            var where = f.LeftM > 0 ? "LEFT" : "RIGHT";
            status = (f.SideZone == HitchZone.Red ? "OFF LINE" : "ALMOST") + $"  -  KINGPIN {Length(Math.Abs(f.LeftM), metric)} {where}";
            color = f.SideZone == HitchZone.Red ? Red : Amber;
        }
        else
        {
            var which = f.UpM > 0 ? "HIGH" : "LOW";
            status = $"HEIGHT OFF  -  TRAILER {Length(Math.Abs(f.UpM), metric)} {which}";
            color = f.HeightZone == HitchZone.Red ? Red : Amber;
        }

        if (f.SlowDown)
        {
            // Over the baseline speed: the banner (red, white text) takes priority over the alignment words.
            status = $"SLOW DOWN  -  {Speed(f.ClosingSpeedMps, metric)}";
            color = White;
        }

        return new AlignReadout
        {
            StatusText = status, StatusColor = color,
            SideText = sideText, SideColor = side,
            GapText = gapText, GapColor = f.BehindM < HitchTolerance.OvershootM ? Red : f.BehindM <= HitchTolerance.HookGapM ? Green : Neutral,
            HeightText = heightText, HeightColor = height,
            AngleText = angle, AngleColor = Neutral,
        };
    }

    public static string Zone(HitchZone z) => z switch { HitchZone.Green => Green, HitchZone.Amber => Amber, _ => Red };

    private static string SideText(double left, bool metric)
    {
        if (Math.Abs(left) < 0.005) return "CENTRED";
        return (left > 0 ? "← " : "→ ") + Length(Math.Abs(left), metric);
    }

    private static string HeightText(double up, bool metric)
    {
        if (Math.Abs(up) < 0.005) return "LEVEL";
        return (up > 0 ? "▲ " : "▼ ") + Length(Math.Abs(up), metric);
    }

    /// <summary>A length in the user's unit: small values in cm / inches, larger in m / feet.</summary>
    public static string Length(double meters, bool metric, bool far = false)
    {
        var m = Math.Abs(meters);
        if (metric)
        {
            if (far && m >= 1000) return $"{m / 1000.0:0.0} km";
            if (m < 1.0) return $"{m * 100.0:0} cm";
            return m < 10.0 ? $"{m:0.0} m" : $"{m:0} m";
        }

        if (far && m >= 1609.344 / 2) return $"{m / 1609.344:0.0} mi";
        var inches = m * MetresToInches;
        if (inches < 36.0) return $"{inches:0} in";
        var feet = m * MetresToFeet;
        return feet < 100.0 ? $"{feet:0.0} ft" : $"{feet:0} ft";
    }

    /// <summary>A speed in the user's unit: mph with a decimal, km/h with one under 10.</summary>
    public static string Speed(double metersPerSecond, bool metric)
    {
        if (metric)
        {
            var kph = metersPerSecond * 3.6;
            return kph < 10.0 ? $"{kph:0.0} km/h" : $"{kph:0} km/h";
        }

        return $"{metersPerSecond / ApproachSpeed.MetresPerSecondPerMph:0.0} mph";
    }

    /// <summary>Where the trailer is, relative to the truck's heading, as a compass-style word.</summary>
    public static string Bearing(HitchFrame f)
    {
        // 0 = straight ahead, positive = toward the truck's left
        var ahead = -f.BehindM;
        var deg = Math.Atan2(f.LeftM, ahead) * 180.0 / Math.PI;
        var a = Math.Abs(deg);
        string word;
        if (a < 22.5) return "AHEAD";
        if (a > 157.5) return "BEHIND";
        var left = deg > 0;
        if (a < 67.5) word = "AHEAD-" + (left ? "LEFT" : "RIGHT");
        else if (a < 112.5) word = left ? "LEFT" : "RIGHT";
        else word = "BEHIND-" + (left ? "LEFT" : "RIGHT");
        return word;
    }
}
