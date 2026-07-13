using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Dyno.App.Converters;

/// <summary>Colors an event log line by its "[LVL ]" prefix from MainWindowViewModel.AddEvent.</summary>
public sealed class EventLevelToBrushConverter : IValueConverter
{
    public static readonly EventLevelToBrushConverter Instance = new();

    private static readonly IBrush Error = new SolidColorBrush(Color.Parse("#FF5C5C"));
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#F5A623"));
    private static readonly IBrush Success = new SolidColorBrush(Color.Parse("#3ECF8E"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#4C8DFF"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8891A0"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string ?? string.Empty;
        return text switch
        {
            _ when text.Contains("[ERR") => Error,
            _ when text.Contains("[WARN") => Warning,
            _ when text.Contains("[OK") => Success,
            _ when text.Contains("[CMD") || text.Contains("[RSP") => Accent,
            _ => Muted,
        };
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    ) => throw new NotSupportedException();
}
