using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TruckHub.Converters;

/// <summary>
/// Turns a 0..1 fraction into a per-segment lit/unlit Visibility, for a discrete segmented bar built
/// from N stacked Border "cells" (ConverterParameter = 1-based segment index, counted from the bar's
/// filled end) - the same segmented-gauge look MainWindow's AdBlue indicator uses, generalized to an
/// arbitrary segment count and bound per-item instead of 4 hand-wired bool properties, since this
/// drives a dynamically-sized per-wheel ItemsControl rather than one fixed gauge.
/// </summary>
public sealed class FractionToSegmentLitConverter : IValueConverter
{
    /// <summary>Total segments in the bar - matches AdBlue's own "lights as soon as the level reaches
    /// into its segment" convention (Math.Ceiling), so a barely-nonzero reading still shows one lit
    /// segment rather than looking completely empty.</summary>
    public int SegmentCount { get; set; } = 6;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var fraction = value is double d ? Math.Clamp(d, 0.0, 1.0) : 0.0;
        var litSegments = (int)Math.Ceiling(fraction * SegmentCount);
        var segmentIndex = parameter is string s && int.TryParse(s, out var i) ? i : 0;
        return segmentIndex <= litSegments ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
