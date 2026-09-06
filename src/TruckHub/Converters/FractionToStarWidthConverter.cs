using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TruckHub.Converters;

/// <summary>
/// Turns a 0..1 fraction into a Star-sized GridLength, for a proportional-fill bar built from two
/// grid columns (fill column + remainder column) rather than a template-restyled ProgressBar - lets
/// the fill sit as a plain colored Border, matching every other flat/borderless indicator already
/// used across the app (AdBlue's segments, warning-light dots) instead of introducing a different
/// visual language just for this one control.
/// </summary>
public sealed class FractionToStarWidthConverter : IValueConverter
{
    /// <summary>Pass ConverterParameter="Invert" on the bar's second (remainder) column so its Star
    /// value is (1-fraction) - Star sizing is proportional to the SUM of every Star column's own
    /// value, so the remainder column can't just be a literal "*" (1 star) or the fill percentage
    /// would be wrong for any fraction other than exactly matching whatever the remainder happens to
    /// be relative to a fixed 1.</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var fraction = value is double d ? Math.Clamp(d, 0.0, 1.0) : 0.0;
        if (parameter is string s && s == "Invert")
        {
            fraction = 1.0 - fraction;
        }
        return new GridLength(fraction, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
