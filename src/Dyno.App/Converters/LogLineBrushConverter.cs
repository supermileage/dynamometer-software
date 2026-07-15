using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Dyno.App.Converters;

/// <summary>
/// Colours one log line, given the line and whether its tab colour-codes by severity. On a
/// colour-coding tab (Errors / Events) it defers to <see cref="EventLevelToBrushConverter"/>; on a
/// plain tab (Console) every line is primary text, because a build's own output carries no
/// <c>[LEVEL]</c> tag and rendering it all grey would only make a long log harder to read.
/// </summary>
public sealed class LogLineBrushConverter : IMultiValueConverter
{
    public static readonly LogLineBrushConverter Instance = new();

    // Matches App.axaml's TextPrimaryColor.
    private static readonly IBrush Plain = new SolidColorBrush(Color.Parse("#E7EAF0"));

    public object Convert(
        IList<object?> values,
        Type targetType,
        object? parameter,
        CultureInfo culture
    )
    {
        var line = values.Count > 0 ? values[0] as string : null;
        var colorize = values.Count > 1 && values[1] is true;
        return colorize
            ? EventLevelToBrushConverter.Instance.Convert(line, targetType, parameter, culture)
            : Plain;
    }
}
