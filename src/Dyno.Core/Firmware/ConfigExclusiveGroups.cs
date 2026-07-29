namespace Dyno.Core.Firmware;

/// <summary>
/// Sets of compile-time switches the firmware refuses to have on at the same time.
///
/// The headers already enforce these with an <c>#error</c>, which is the authority — this list
/// exists so the SysConfig page can keep a user out of that build rather than let them save a
/// combination that fails at compile time, minutes later, with a message they have to go and read.
///
/// Membership is exclusive, not required: every switch in a group may be off. That is a real
/// configuration rather than an oversight — with no display driver enabled the display task parks
/// and nothing drives SPI1, which is how the panel gets ruled in or out of a fault elsewhere on the
/// board. So this cannot be modelled as a radio group.
/// </summary>
public static class ConfigExclusiveGroups
{
    public static readonly IReadOnlyList<IReadOnlyList<string>> Groups =
    [
        // debug.h: "At most one display driver may be enabled". One panel is soldered to a given
        // board, and the two share SPI1 and its chip select.
        ["LUMEX_LCD_TASK_ENABLE", "ILI9341_LCD_TASK_ENABLE"],
    ];

    /// <summary>The other switches that must go off when <paramref name="name"/> is turned on.
    /// Empty for a setting that is in no group, which is nearly all of them.</summary>
    public static IReadOnlyList<string> Siblings(string name) =>
        Groups
            .Where(group => group.Contains(name, StringComparer.Ordinal))
            .SelectMany(group => group.Where(member => member != name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
