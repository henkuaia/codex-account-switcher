using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;

namespace CodexAccountSwitcher.Converters;

public sealed class QuotaRemainingBrushConverter : IValueConverter
{
    private static readonly MediaColor LowColor = MediaColor.FromRgb(0xD9, 0x53, 0x4F);
    private static readonly MediaColor MiddleColor = MediaColor.FromRgb(0xE6, 0xA2, 0x3C);
    private static readonly MediaColor HighColor = MediaColor.FromRgb(0x16, 0xB3, 0x64);
    private static readonly SolidColorBrush NeutralBrush = CreateBrush(
        MediaColor.FromRgb(0x66, 0x71, 0x78));

    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        if (!TryReadFiniteNumber(value, out var number))
        {
            return NeutralBrush;
        }

        var remaining = Math.Clamp(number, 0, 100);
        return remaining <= 50
            ? CreateBrush(Interpolate(LowColor, MiddleColor, remaining / 50))
            : CreateBrush(Interpolate(MiddleColor, HighColor, (remaining - 50) / 50));
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();

    private static bool TryReadFiniteNumber(object value, out double number)
    {
        number = value switch
        {
            byte current => current,
            sbyte current => current,
            short current => current,
            ushort current => current,
            int current => current,
            uint current => current,
            long current => current,
            ulong current => current,
            float current => current,
            double current => current,
            decimal current => (double)current,
            _ => double.NaN,
        };
        return double.IsFinite(number);
    }

    private static MediaColor Interpolate(
        MediaColor start,
        MediaColor end,
        double amount) => MediaColor.FromRgb(
        Interpolate(start.R, end.R, amount),
        Interpolate(start.G, end.G, amount),
        Interpolate(start.B, end.B, amount));

    private static byte Interpolate(byte start, byte end, double amount) =>
        (byte)Math.Round(start + ((end - start) * amount), MidpointRounding.AwayFromZero);

    private static SolidColorBrush CreateBrush(MediaColor color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
