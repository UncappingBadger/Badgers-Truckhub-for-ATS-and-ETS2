using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TruckHub.Controls;

/// <summary>One drum of a mechanical-style odometer - see OdometerDigit.xaml's own comment for why
/// this shows the digit as plain text rather than a scrolling strip.</summary>
public partial class OdometerDigit : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(OdometerDigit),
        new PropertyMetadata(0.0, OnValueChanged));

    /// <summary>0-9. Only the integer part is displayed - see MainViewModel's OdometerDigit* comment
    /// for why every digit (including tenths) is a clean whole number here rather than a continuous
    /// fractional value.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public OdometerDigit()
    {
        InitializeComponent();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not double value)
        {
            return;
        }

        var digit = ((int)Math.Floor(value)) % 10;
        if (digit < 0)
        {
            digit += 10;
        }

        var control = (OdometerDigit)d;
        var newText = digit.ToString();
        if (control.DigitText.Text == newText)
        {
            return;
        }

        control.DigitText.Text = newText;

        // Brief amber flash on every actual digit change - the "tick" - rather than an always-on
        // continuous animation, so a digit that's been sitting still for miles doesn't look like
        // it's doing anything until the moment it genuinely changes.
        var brush = new SolidColorBrush(Colors.White);
        control.DigitText.Foreground = brush;
        var flash = new ColorAnimation
        {
            From = (Color)ColorConverter.ConvertFromString("#FFC24C"),
            To = Colors.White,
            Duration = TimeSpan.FromMilliseconds(350),
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, flash);
    }
}
