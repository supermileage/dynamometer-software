using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Dyno.App.Converters;

/// <summary>Green when connected, muted grey otherwise — drives the connection status dot.</summary>
public sealed class BoolToStatusBrushConverter : IValueConverter
{
    public static readonly BoolToStatusBrushConverter Instance = new();

    private static readonly IBrush Connected = new SolidColorBrush(Color.Parse("#3ECF8E"));
    private static readonly IBrush Disconnected = new SolidColorBrush(Color.Parse("#8892A2"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Connected : Disconnected;

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();
}
