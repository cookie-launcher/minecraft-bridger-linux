using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Bridger.Avalonia;

// OdaModel.DurumArkaRengiHex (#RRGGBB) → fırça dönüştürücü (rozet arka planı).
public sealed class HexFircaCevirici : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try { return value is string s && s.Length >= 7 ? Brush.Parse(s) : null; }
        catch { return null; }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
