using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using TruckHub.Services;

namespace TruckHub.Controls;

/// <summary>
/// The ALIGN window's picture: a synthetic rear-camera view in the style of a pickup's reversing camera. There is no
/// camera feed, so the scene is drawn from the geometry - and what is drawn is exactly what the telemetry says is there.
///
/// Reading it: the colour-graded pair of guide lines (red near, yellow, green far, with cross ticks) and the dashed
/// centre line are the truck's own path back from the fifth wheel. They bend with the steering wheel to show where the
/// fifth wheel will go if you keep it where it is (straight back until the truck's steering has been learned, while
/// driving forward, and when hooked up). The trailer is drawn as a solid, see-through
/// 3D model standing where the telemetry puts it, with its kingpin marked by the ring (a stem runs up from the ring to
/// the kingpin's real height). The bright line on the ground is the trailer's centreline. Line the ring up on the dashed
/// centre line (it turns green inside ~5 cm) and keep backing; the little gauge on the right is the kingpin's height
/// against the fifth wheel.
///
/// Perspective: a plain pinhole whose camera sits CameraDepth metres ahead of the fifth wheel at CameraHeight above the
/// ground, looking straight back. A point (right, back, height) maps to
///   x = cx + right * k * s,   y = horizon + (near - horizon) * s * (1 - height / CameraHeight),   s = d0 / (back + d0),
/// so straight lines stay straight and a point 4 m up lands above the horizon exactly as it would for a camera 2.6 m up.
/// back = 0 (the fifth wheel) is pinned near the bottom of the view.
///
/// The trailer is a few convex prisms (body, chassis, ...). Faces turned away from the camera are skipped, the rest are
/// drawn far-to-near, clipped against a near plane so a trailer that is about to swallow the camera does not blow up.
/// </summary>
public sealed class AlignCameraView : FrameworkElement
{
    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
        nameof(Frame), typeof(HitchFrame), typeof(AlignCameraView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public HitchFrame? Frame
    {
        get => (HitchFrame?)GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    // ---- view layout
    private const double HorizonFraction = 0.24;   // horizon height, fraction of the control
    private const double NearFraction = 0.88;      // where "0 m behind the fifth wheel" sits on the ground
    private const double CameraDepth = 2.4;        // d0: the virtual camera is this far ahead of the fifth wheel, metres
    private const double CameraHeight = 2.6;       // ... and this high above the ground
    private const double SideFraction = 0.32;      // fraction of the width one metre spans at the fifth wheel
    private const double FarLimit = 60.0;
    private const double NearClip = -1.1;          // nothing nearer than this (back) is drawn; Scale() stops at -1.2

    // ---- the corridor (guide lines)
    private const double CorridorHalfWidth = 0.30;
    private const double RedUntil = 1.5, YellowUntil = 4.0, GuideEnd = 30.0;

    private static readonly Color Sky1 = Color.FromRgb(0x0C, 0x0E, 0x12), Sky2 = Color.FromRgb(0x1B, 0x1F, 0x27);
    private static readonly Color GroundFar = Color.FromRgb(0x22, 0x24, 0x28), GroundNear = Color.FromRgb(0x3C, 0x3E, 0x44);
    private static readonly Color GuideRed = Color.FromRgb(0xE8, 0x3A, 0x30), GuideYellow = Color.FromRgb(0xF4, 0xC4, 0x2E), GuideGreen = Color.FromRgb(0x3D, 0xCF, 0x58);
    private static readonly Color ZoneGreen = Color.FromRgb(0x5F, 0xD8, 0x5F), ZoneAmber = Color.FromRgb(0xFF, 0xC2, 0x4C), ZoneRed = Color.FromRgb(0xFF, 0x4C, 0x4C);
    private static readonly Color AxisColor = Color.FromRgb(0x5C, 0xE1, 0xFF);

    private static readonly Color BoxColor = Color.FromRgb(0x86, 0xA8, 0xD8), ReeferColor = Color.FromRgb(0xA9, 0xD6, 0xEA),
        ContainerColor = Color.FromRgb(0xD3, 0x8E, 0x4E), TankerColor = Color.FromRgb(0xC2, 0xCB, 0xD4),
        DeckColor = Color.FromRgb(0x9A, 0x88, 0x68), ChassisColor = Color.FromRgb(0x4A, 0x52, 0x5C);

    /// <summary>A point in the camera world: right of the truck's centreline, back from the fifth wheel, height above the ground (metres).</summary>
    private readonly record struct V3(double R, double B, double H)
    {
        public static V3 operator -(V3 a, V3 b) => new(a.R - b.R, a.B - b.B, a.H - b.H);
        public double Dot(V3 o) => R * o.R + B * o.B + H * o.H;
        public double Length => Math.Sqrt(Dot(this));
    }

    private readonly struct Layout
    {
        public readonly double W, H, Cx, HorizonY, NearY, K;

        public Layout(double w, double h)
        {
            W = w; H = h; Cx = w / 2.0;
            HorizonY = h * HorizonFraction;
            NearY = h * NearFraction;
            K = w * SideFraction;                               // px per metre at the fifth wheel
        }

        /// <summary>Depth factor: 1 at the fifth wheel, falling with distance back; above 1 ahead of it (stops short of the camera).</summary>
        public double Scale(double back) => CameraDepth / (Math.Max(back, -1.2) + CameraDepth);

        public Point Project(double right, double back, double height = 0.0)
        {
            var s = Scale(back);
            return new Point(Cx + right * K * s, HorizonY + (NearY - HorizonY) * s * (1.0 - height / CameraHeight));
        }

        public Point Project(V3 p) => Project(p.R, p.B, p.H);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 60 || h < 40)
        {
            return;
        }

        var lay = new Layout(w, h);
        var frame = Frame;

        DrawBackdrop(dc, lay);
        DrawGroundGrid(dc, lay);

        var live = frame is not null && frame.State is HitchState.Approaching or HitchState.Coupled;
        var inView = live && frame!.InRearView;

        if (inView)
        {
            DrawTrailer(dc, lay, frame!);
        }

        DrawGuides(dc, lay, frame?.Path ?? ReversePath.Straight);

        if (inView)
        {
            DrawTrailerAxis(dc, lay, frame!);
            DrawKingpin(dc, lay, frame!);
            DrawHeightGauge(dc, lay, frame!);
            if (frame!.State == HitchState.Coupled)
            {
                DrawCoupledWash(dc, lay);
            }
            else if (frame.BehindM < HitchTolerance.OvershootM)
            {
                // Gone too far under the trailer: a red wash along the bottom.
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 0xFF, 0x30, 0x30)), null, new Rect(0, lay.H * 0.78, lay.W, lay.H * 0.22));
            }
        }
        else if (live)
        {
            DrawOffViewHint(dc, lay, frame!);
        }
        else
        {
            // Nothing to show: dim the empty road; the status text lives in the host's overlay.
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, new Rect(0, 0, lay.W, lay.H));
        }

        if (frame is { SlowDown: true })
        {
            DrawSlowDownEdge(dc, lay);
        }
    }

    /// <summary>
    /// Backing in over the baseline speed: a red frame pulsing around the whole picture, with the edges washed red, so it
    /// registers while your eyes are on the pin. Pulses with the clock (about 1.4 times a second); the window redraws ~30
    /// times a second while it is open, so no timer of its own.
    /// </summary>
    private static void DrawSlowDownEdge(DrawingContext dc, Layout lay)
    {
        var phase = (Environment.TickCount64 % 700) / 700.0;
        var pulse = 0.5 + 0.5 * Math.Sin(phase * 2.0 * Math.PI);
        var edge = (byte)(130 + 125 * pulse);

        var wash = (byte)(40 + 70 * pulse);
        var side = lay.W * 0.12;
        foreach (var left in new[] { true, false })
        {
            var brush = new LinearGradientBrush(
                Color.FromArgb(wash, 0xFF, 0x20, 0x20), Color.FromArgb(0, 0xFF, 0x20, 0x20), new Point(left ? 0 : 1, 0), new Point(left ? 1 : 0, 0));
            dc.DrawRectangle(brush, null, new Rect(left ? 0 : lay.W - side, 0, side, lay.H));
        }

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(edge, 0xFF, 0x30, 0x30)), 5);
        dc.DrawRectangle(null, pen, new Rect(2.5, 2.5, Math.Max(0, lay.W - 5), Math.Max(0, lay.H - 5)));
    }

    // ------------------------------------------------------------------ scene

    private static void DrawBackdrop(DrawingContext dc, Layout lay)
    {
        dc.DrawRectangle(new LinearGradientBrush(Sky1, Sky2, 90), null, new Rect(0, 0, lay.W, lay.HorizonY));
        dc.DrawRectangle(new LinearGradientBrush(GroundFar, GroundNear, 90), null, new Rect(0, lay.HorizonY, lay.W, lay.H - lay.HorizonY));
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1), new Point(0, lay.HorizonY), new Point(lay.W, lay.HorizonY));
    }

    private static void DrawGroundGrid(DrawingContext dc, Layout lay)
    {
        var faint = new Pen(new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)), 1);
        foreach (var back in new[] { 0.0, 1.0, 2.0, 3.0, 4.5, 6.0, 8.0, 11.0, 15.0, 20.0, 28.0 })
        {
            var y = lay.Project(0, back).Y;
            dc.DrawLine(faint, new Point(0, y), new Point(lay.W, y));
        }

        for (var r = -6; r <= 6; r++)
        {
            if (r == 0) continue;
            dc.DrawLine(faint, lay.Project(r, 0), lay.Project(r, 40));
        }
    }

    /// <summary>
    /// The guide lines and the dashed centre line follow <paramref name="path"/> - where the fifth wheel will go at the
    /// current steering - so they bend as the wheel is turned. Straight (the default path) they are exactly the truck's
    /// centreline back from the fifth wheel. Distances for the colours and ticks are metres of travel along the path.
    /// </summary>
    private static void DrawGuides(DrawingContext dc, Layout lay, ReversePath path)
    {
        var end = Math.Min(GuideEnd, path.MaxU);
        foreach (var side in new[] { -1.0, 1.0 })
        {
            var offset = side * CorridorHalfWidth;
            DrawGuideStretch(dc, lay, path, offset, -0.4, RedUntil, GuideRed);
            DrawGuideStretch(dc, lay, path, offset, RedUntil, YellowUntil, GuideYellow);
            DrawGuideStretch(dc, lay, path, offset, YellowUntil, end, GuideGreen);

            // Cross ticks where the colour changes (and a couple further out), pointing in toward the centre.
            foreach (var tick in new[] { RedUntil, YellowUntil, 8.0, 14.0 })
            {
                if (tick > end)
                {
                    continue;
                }

                var color = tick <= RedUntil ? GuideYellow : GuideGreen;
                var p = path.At(tick);
                var outer = p.Offset(offset);
                var inner = p.Offset(offset - side * 0.16);
                dc.DrawLine(new Pen(new SolidColorBrush(color), 2 + 3 * lay.Scale(p.Back)), lay.Project(outer.Right, outer.Back), lay.Project(inner.Right, inner.Back));
            }
        }

        // Dashed centre line: perspective-correct short dashes every 0.6 m along the path.
        for (var u = 0.0; u < end; u += 0.6)
        {
            var a = path.At(u);
            var b = path.At(Math.Min(u + 0.3, end));
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(215, 255, 255, 255)), 1.2 + 3 * lay.Scale(a.Back)) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
            dc.DrawLine(pen, lay.Project(a.Right, a.Back), lay.Project(b.Right, b.Back));
        }
    }

    /// <summary>One colour of one guide line, from path distance <paramref name="from"/> to <paramref name="to"/>, as a run of short segments so a bend shows as a curve.</summary>
    private static void DrawGuideStretch(DrawingContext dc, Layout lay, ReversePath path, double offset, double from, double to, Color color)
    {
        to = Math.Min(to, path.MaxU);
        if (to <= from)
        {
            return;
        }

        var start = path.At(from);
        var pen = new Pen(new SolidColorBrush(color), 2.0 + 6.0 * lay.Scale(start.Back)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        var u = from;
        var prev = start.Offset(offset);
        while (u < to)
        {
            u = Math.Min(to, u + (u < 8.0 ? 0.25 : 0.5));
            var cur = path.At(u).Offset(offset);
            dc.DrawLine(pen, lay.Project(prev.Right, prev.Back), lay.Project(cur.Right, cur.Back));
            prev = cur;
        }
    }

    // ------------------------------------------------------------------ the trailer

    /// <summary>One flat face of the trailer model: its corners, which way it faces, and how to paint it.</summary>
    private sealed class Face
    {
        public List<V3> Pts = new();
        public V3 Normal;
        public Color Base;
        public byte Alpha = 150;
        public List<(V3 a, V3 b)> Ribs = new();
    }

    private static void DrawTrailer(DrawingContext dc, Layout lay, HitchFrame f)
    {
        // Hooked up, the camera is inside the trailer's nose and a model of it would only be a muddy wall - the guides, the
        // centreline and the green wash say all there is to say.
        if (f.State == HitchState.Coupled)
        {
            return;
        }

        var shape = f.Shape;
        var cam = new V3(0, -CameraDepth, CameraHeight);

        // The trailer's own axes on the ground, in the camera frame: 'a' points from the kingpin toward the nose (toward
        // the truck), 'n' is sideways. U = distance behind the kingpin along the trailer, V = sideways, H = height.
        V3 kp = new(-f.LeftM, f.BehindM, 0);
        double ar = f.AxisRight, ab = f.AxisBack;
        double nr = ab, nb = -ar;
        V3 At(double u, double v, double h) => new(kp.R - ar * u + nr * v, kp.B - ab * u + nb * v, h);
        var forward = new V3(ar, ab, 0);        // outward normal of the nose
        V3 sideAxis = new(nr, nb, 0);

        var faces = new List<Face>();
        double u0 = -shape.NoseAheadM, u1 = shape.LengthM - shape.NoseAheadM;
        var halfW = shape.WidthM / 2;

        void Prism((double v, double h)[] section, double a0, double a1, Color color, byte alpha, double ribEvery = 0)
        {
            double cv = 0, ch = 0;
            foreach (var p in section) { cv += p.v; ch += p.h; }
            cv /= section.Length; ch /= section.Length;

            for (var i = 0; i < section.Length; i++)
            {
                var p = section[i];
                var q = section[(i + 1) % section.Length];
                double dv = q.v - p.v, dh = q.h - p.h;
                var len = Math.Sqrt(dv * dv + dh * dh);
                if (len < 1e-6) continue;
                double nv = dh / len, nh = -dv / len;
                if (nv * ((p.v + q.v) / 2 - cv) + nh * ((p.h + q.h) / 2 - ch) < 0) { nv = -nv; nh = -nh; }

                var face = new Face { Base = color, Alpha = alpha, Normal = new V3(nv * nr, nv * nb, nh) };
                face.Pts.Add(At(a0, p.v, p.h));
                face.Pts.Add(At(a1, p.v, p.h));
                face.Pts.Add(At(a1, q.v, q.h));
                face.Pts.Add(At(a0, q.v, q.h));
                if (ribEvery > 0 && Math.Abs(dv) < 1e-6 && Math.Abs(dh) > 0.3)
                {
                    for (var u = a0 + ribEvery; u < a1 - 0.2; u += ribEvery)
                    {
                        face.Ribs.Add((At(u, p.v, p.h), At(u, q.v, q.h)));
                    }
                }

                faces.Add(face);
            }

            var front = new Face { Base = color, Alpha = alpha, Normal = forward };
            var rear = new Face { Base = color, Alpha = alpha, Normal = new V3(-ar, -ab, 0) };
            foreach (var p in section)
            {
                front.Pts.Add(At(a0, p.v, p.h));
                rear.Pts.Add(At(a1, p.v, p.h));
            }

            faces.Add(front);
            faces.Add(rear);
        }

        (double v, double h)[] Rect(double halfWidth, double h0, double h1) =>
            new[] { (-halfWidth, h0), (halfWidth, h0), (halfWidth, h1), (-halfWidth, h1) };

        // chassis rails under every body
        Prism(Rect(Math.Min(halfW - 0.2, 1.0), shape.FloorM - 0.42, shape.FloorM - 0.05), u0 + 0.1, u1 - 0.2, ChassisColor, 200);

        switch (shape.Body)
        {
            case TrailerBody.Flatbed:
                Prism(Rect(halfW, shape.FloorM, shape.TopM), u0, u1, DeckColor, 170);
                break;
            case TrailerBody.Container:
                Prism(Rect(halfW, shape.FloorM, shape.TopM), u0 + 0.15, u1 - 0.15, ContainerColor, 150, 1.0);
                break;
            case TrailerBody.Tanker:
            {
                var cx = 0.0;
                var cy = (shape.FloorM + shape.TopM) / 2;
                var ry = (shape.TopM - shape.FloorM) / 2;
                var rx = halfW * 0.96;
                const int segments = 14;
                var section = new (double v, double h)[segments];
                for (var i = 0; i < segments; i++)
                {
                    var t = 2 * Math.PI * i / segments;
                    section[i] = (cx + rx * Math.Cos(t), cy + ry * Math.Sin(t));
                }

                Prism(section, u0 + 0.1, u1 - 0.1, TankerColor, 150);
                break;
            }
            case TrailerBody.Reefer:
                Prism(Rect(halfW, shape.FloorM, shape.TopM), u0, u1, ReeferColor, 150, 1.3);
                break;
            default:
                Prism(Rect(halfW, shape.FloorM, shape.TopM), u0, u1, BoxColor, 150, 1.3);
                break;
        }

        // ---- paint: skip faces turned away from the camera, far to near
        var drawn = new List<(Face face, double dist, List<V3> pts)>();
        foreach (var face in faces)
        {
            var centre = new V3(0, 0, 0);
            double sr = 0, sb = 0, sh = 0;
            foreach (var p in face.Pts) { sr += p.R; sb += p.B; sh += p.H; }
            centre = new V3(sr / face.Pts.Count, sb / face.Pts.Count, sh / face.Pts.Count);
            if (face.Normal.Dot(cam - centre) <= 0)
            {
                continue;
            }

            var clipped = ClipNear(face.Pts, NearClip);
            if (clipped.Count < 3)
            {
                continue;
            }

            drawn.Add((face, (cam - centre).Length, clipped));
        }

        drawn.Sort((x, y) => y.dist.CompareTo(x.dist));
        foreach (var (face, dist, pts) in drawn)
        {
            var toCam = cam - new V3(pts[0].R, pts[0].B, pts[0].H);
            var facing = Math.Clamp(Math.Abs(face.Normal.Dot(toCam)) / Math.Max(toCam.Length, 1e-6), 0, 1);
            var shade = 0.55 + 0.45 * facing;
            // A face close to the camera (the nose, when you are nearly there) is huge and would hide the ground, the guides
            // and the target exactly when they matter - so the nearer it is, the more see-through it gets.
            var fade = Math.Clamp((dist - 1.2) / 3.5, 0.28, 1.0);
            var fill = Color.FromArgb((byte)(face.Alpha * fade), (byte)(face.Base.R * shade), (byte)(face.Base.G * shade), (byte)(face.Base.B * shade));
            var edge = Color.FromArgb((byte)(215 * Math.Max(fade, 0.55)), (byte)Math.Min(255, face.Base.R * 0.55 + 120), (byte)Math.Min(255, face.Base.G * 0.55 + 120), (byte)Math.Min(255, face.Base.B * 0.55 + 120));

            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(lay.Project(pts[0]), true, true);
                for (var i = 1; i < pts.Count; i++)
                {
                    g.LineTo(lay.Project(pts[i]), true, false);
                }
            }

            geo.Freeze();
            dc.DrawGeometry(new SolidColorBrush(fill), new Pen(new SolidColorBrush(edge), 1.1) { LineJoin = PenLineJoin.Round }, geo);

            var rib = new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1);
            foreach (var (ra, rb) in face.Ribs)
            {
                if (ra.B >= NearClip && rb.B >= NearClip)
                {
                    dc.DrawLine(rib, lay.Project(ra), lay.Project(rb));
                }
            }
        }

        // ---- wheels on the side(s) facing the camera
        var tyre = new SolidColorBrush(Color.FromArgb(235, 0x14, 0x16, 0x19));
        var tyreEdge = new Pen(new SolidColorBrush(Color.FromArgb(200, 0x6A, 0x70, 0x78)), 1);
        var tyreR = shape.WheelRadiusM;
        foreach (var u in shape.AxleU)
        {
            foreach (var side in new[] { -1.0, 1.0 })
            {
                // outside of the wheel track (dual tyres sit just beyond the body line)
                var v = side * (shape.TrackHalfM + 0.12);
                var outward = new V3(nr * side, nb * side, 0);
                var mid = At(u, v, tyreR);
                if (outward.Dot(cam - mid) <= 0)
                {
                    continue;
                }

                var quad = ClipNear(new List<V3> { At(u - tyreR, v, 0), At(u + tyreR, v, 0), At(u + tyreR, v, 2 * tyreR), At(u - tyreR, v, 2 * tyreR) }, NearClip);
                if (quad.Count < 3) continue;
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(lay.Project(quad[0]), true, true);
                    for (var k = 1; k < quad.Count; k++) g.LineTo(lay.Project(quad[k]), true, false);
                }

                geo.Freeze();
                dc.DrawGeometry(tyre, tyreEdge, geo);
            }
        }

        // ---- landing gear: down while the trailer stands on its own
        if (f.State == HitchState.Approaching)
        {
            var leg = new Pen(new SolidColorBrush(Color.FromArgb(230, 0xA9, 0xB1, 0xBB)), 3) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Round };
            foreach (var side in new[] { -1.0, 1.0 })
            {
                var top = At(1.7, side * 0.95, shape.FloorM - 0.05);
                var foot = At(1.7, side * 0.95, 0.0);
                if (top.B >= NearClip && foot.B >= NearClip)
                {
                    dc.DrawLine(leg, lay.Project(top), lay.Project(foot));
                    dc.DrawLine(leg, lay.Project(At(1.7, side * 0.95 - 0.18, 0.0)), lay.Project(At(1.7, side * 0.95 + 0.18, 0.0)));
                }
            }
        }
    }

    /// <summary>Clips a 3D polygon to back >= limit (Sutherland-Hodgman against one half-space) so nothing is drawn at or behind the camera.</summary>
    private static List<V3> ClipNear(List<V3> poly, double limit)
    {
        var result = new List<V3>();
        for (var i = 0; i < poly.Count; i++)
        {
            var cur = poly[i];
            var prev = poly[(i + poly.Count - 1) % poly.Count];
            var curIn = cur.B >= limit;
            var prevIn = prev.B >= limit;
            if (curIn != prevIn)
            {
                var t = (limit - prev.B) / (cur.B - prev.B);
                result.Add(new V3(prev.R + (cur.R - prev.R) * t, limit, prev.H + (cur.H - prev.H) * t));
            }

            if (curIn)
            {
                result.Add(cur);
            }
        }

        return result;
    }

    private static void DrawTrailerAxis(DrawingContext dc, Layout lay, HitchFrame f)
    {
        double kr = -f.LeftM, kb = f.BehindM;
        double ar = f.AxisRight, ab = f.AxisBack;

        // The trailer's centreline on the ground through the kingpin: from just ahead of the nose to well behind.
        double tMin = -3.0, tMax = 25.0;
        ClipParam(ref tMin, ref tMax, kb, ab, NearClip, FarLimit);
        if (tMax - tMin < 1e-6)
        {
            return;
        }

        var pts = new List<Point>();
        for (var i = 0; i <= 16; i++)
        {
            var t = tMin + (tMax - tMin) * i / 16.0;
            pts.Add(lay.Project(kr + ar * t, kb + ab * t));
        }

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(pts[0], false, false);
            for (var i = 1; i < pts.Count; i++) g.LineTo(pts[i], true, false);
        }

        geo.Freeze();
        var near = lay.Scale(Math.Max(kb, 0));
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(200, 0x06, 0x1E, 0x26)), 6 + 4 * near) { LineJoin = PenLineJoin.Round }, geo);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(AxisColor), 2.5 + 2.5 * near) { LineJoin = PenLineJoin.Round }, geo);
    }

    private static void ClipParam(ref double tMin, ref double tMax, double b0, double db, double bLow, double bHigh)
    {
        if (Math.Abs(db) < 1e-9)
        {
            if (b0 < bLow || b0 > bHigh) tMax = tMin;
            return;
        }

        var t1 = (bLow - b0) / db;
        var t2 = (bHigh - b0) / db;
        if (t1 > t2) (t1, t2) = (t2, t1);
        tMin = Math.Max(tMin, t1);
        tMax = Math.Min(tMax, t2);
    }

    private static Color ZoneColor(HitchZone z) => z switch { HitchZone.Green => ZoneGreen, HitchZone.Amber => ZoneAmber, _ => ZoneRed };

    private static void DrawKingpin(DrawingContext dc, Layout lay, HitchFrame f)
    {
        var p = lay.Project(-f.LeftM, f.BehindM);
        var scale = lay.Scale(f.BehindM);
        var radius = Math.Clamp(0.16 * lay.K * scale, 7.0, 26.0);
        var color = ZoneColor(f.SideZone);

        // Off the sides of the picture: pin the marker to the edge with a pointer instead of losing it.
        var edge = 16.0;
        var clamped = new Point(Math.Clamp(p.X, edge, lay.W - edge), Math.Clamp(p.Y, lay.HorizonY + 4, lay.H - 6));
        var offLeft = p.X < edge;
        var offRight = p.X > lay.W - edge;

        // A stem from the ring up to the kingpin's real height under the trailer: ties the ground marker to the pin.
        if (f.State == HitchState.Approaching && !offLeft && !offRight && f.BehindM >= NearClip)
        {
            var top = lay.Project(-f.LeftM, f.BehindM, f.Shape.FloorM);
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), 1.6) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) }, p, top);
            dc.DrawEllipse(new SolidColorBrush(color), new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 1), top, 3.2, 3.2);
        }

        var ring = new Pen(new SolidColorBrush(color), 3.5);
        var shadow = new Pen(new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)), 6.5);
        dc.DrawEllipse(null, shadow, clamped, radius, radius);
        dc.DrawEllipse(null, ring, clamped, radius, radius);
        var fine = new Pen(new SolidColorBrush(color), 1.6);
        dc.DrawLine(fine, new Point(clamped.X - radius * 1.5, clamped.Y), new Point(clamped.X + radius * 1.5, clamped.Y));
        dc.DrawLine(fine, new Point(clamped.X, clamped.Y - radius * 1.5), new Point(clamped.X, clamped.Y + radius * 1.5));
        dc.DrawEllipse(new SolidColorBrush(color), null, clamped, 2.6, 2.6);

        if (offLeft || offRight)
        {
            var dir = offLeft ? -1.0 : 1.0;
            var tip = new Point(clamped.X + dir * (radius + 12), clamped.Y);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(tip, true, true);
                g.LineTo(new Point(tip.X - dir * 11, tip.Y - 8), true, false);
                g.LineTo(new Point(tip.X - dir * 11, tip.Y + 8), true, false);
            }

            geo.Freeze();
            dc.DrawGeometry(new SolidColorBrush(color), null, geo);
        }
    }

    private static void DrawHeightGauge(DrawingContext dc, Layout lay, HitchFrame f)
    {
        const double range = 0.15;                       // +-15 cm
        var x = lay.W - 22;
        var top = lay.H * 0.30;
        var bottom = lay.H * 0.80;
        var mid = (top + bottom) / 2;
        var half = (bottom - top) / 2;

        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, new Rect(x - 9, top - 8, 18, bottom - top + 16), 5, 5);
        // amber band (+-10 cm) then green band (+-5 cm)
        var a = half * HitchTolerance.AmberHeightM / range;
        var gr = half * HitchTolerance.GreenHeightM / range;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, ZoneAmber.R, ZoneAmber.G, ZoneAmber.B)), null, new Rect(x - 3.5, mid - a, 7, a * 2));
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, ZoneGreen.R, ZoneGreen.G, ZoneGreen.B)), null, new Rect(x - 3.5, mid - gr, 7, gr * 2));
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1), new Point(x - 7, mid), new Point(x + 7, mid));

        var y = mid - Math.Clamp(f.UpM / range, -1, 1) * half;
        var color = ZoneColor(f.HeightZone);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(x - 12, y), true, true);
            g.LineTo(new Point(x - 22, y - 7), true, false);
            g.LineTo(new Point(x - 22, y + 7), true, false);
        }

        geo.Freeze();
        dc.DrawGeometry(new SolidColorBrush(color), new Pen(new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), 1), geo);
    }

    private static void DrawCoupledWash(DrawingContext dc, Layout lay)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, ZoneGreen.R, ZoneGreen.G, ZoneGreen.B)), null, new Rect(0, 0, lay.W, lay.H));
        var cx = lay.W / 2;
        var cy = lay.H * 0.56;
        var s = Math.Min(lay.W, lay.H) * 0.13;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(cx - s, cy), false, false);
            g.LineTo(new Point(cx - s * 0.3, cy + s * 0.75), true, false);
            g.LineTo(new Point(cx + s * 1.1, cy - s * 0.8), true, false);
        }

        geo.Freeze();
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(230, ZoneGreen.R, ZoneGreen.G, ZoneGreen.B)), s * 0.28) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geo);
    }

    /// <summary>The trailer is beside or ahead of the truck, or very far: the rear view cannot show it, so point toward it.</summary>
    private static void DrawOffViewHint(DrawingContext dc, Layout lay, HitchFrame f)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), null, new Rect(0, 0, lay.W, lay.H));

        // Bearing of the kingpin from the fifth wheel: 0 = straight back (down the screen), positive = to the truck's right.
        var bearing = Math.Atan2(-f.LeftM, f.BehindM);            // atan2(right, back)
        var cx = lay.W / 2;
        var cy = lay.H * 0.52;
        var len = Math.Min(lay.W, lay.H) * 0.22;
        // On screen "back" is DOWN and "right" is RIGHT.
        var dir = new Vector(Math.Sin(bearing), Math.Cos(bearing));
        var tip = new Point(cx + dir.X * len, cy + dir.Y * len);
        var tail = new Point(cx - dir.X * len * 0.6, cy - dir.Y * len * 0.6);
        var color = Color.FromRgb(0x5C, 0xE1, 0xFF);

        dc.DrawLine(new Pen(new SolidColorBrush(color), 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Flat }, tail, tip);
        var perp = new Vector(-dir.Y, dir.X);
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(new Point(tip.X + dir.X * 14, tip.Y + dir.Y * 14), true, true);
            g.LineTo(new Point(tip.X + perp.X * 12, tip.Y + perp.Y * 12), true, false);
            g.LineTo(new Point(tip.X - perp.X * 12, tip.Y - perp.Y * 12), true, false);
        }

        head.Freeze();
        dc.DrawGeometry(new SolidColorBrush(color), null, head);
        // a small truck marker at the centre: the arrow points from the truck toward the trailer (down = behind you)
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), null, tail, 5, 5);
    }
}
