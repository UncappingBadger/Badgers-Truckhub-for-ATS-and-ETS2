using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TruckHub.Converters;

/// <summary>
/// Like the standard BooleanToVisibilityConverter, but hides via Visibility.Hidden instead of
/// Collapsed - the element keeps its layout space reserved instead of disappearing, which matters
/// here because the whole card sits in a Viewbox that rescales to fit the content's aspect ratio;
/// a Collapsed row changes that aspect ratio and makes the entire card visibly resize.
/// </summary>
public sealed class BoolToHiddenConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Hidden;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
