using Dyno.Core.Firmware;

namespace Dyno.App.ViewModels;

/// <summary>One of the three ways to get firmware onto the board, as the Firmware page offers it.
/// The differences that decide which one a user can actually use are physical — do you own a probe,
/// can you reach the BOOT0 jumper — so they are spelled out on the card rather than left to the
/// README.</summary>
public sealed class FlashMethodChoice
{
    public required FlashMethod Method { get; init; }

    /// <summary>What the connection is, in the words someone would use at the bench.</summary>
    public required string Title { get; init; }

    /// <summary>What it needs, and what it costs — the sentence that decides the choice.</summary>
    public required string Subtitle { get; init; }

    /// <summary>True when the chip's ROM bootloader has to be entered by hand first.</summary>
    public bool NeedsBootloader => FirmwareCommands.NeedsBootloader(Method);

    public static IReadOnlyList<FlashMethodChoice> All =>
        [
            new()
            {
                Method = FlashMethod.Swd,
                Title = "SWD probe",
                Subtitle =
                    "An ST-Link (or J-Link) on the SWD header. The quickest, and the only one that "
                    + "flashes a running board with no jumper to move.",
            },
            new()
            {
                Method = FlashMethod.Dfu,
                Title = "USB DFU",
                Subtitle =
                    "The USB cable you already have — no probe. Goes through the chip's built-in "
                    + "bootloader.",
            },
            new()
            {
                Method = FlashMethod.Uart,
                Title = "UART",
                Subtitle =
                    "A USB-to-serial adapter on the UART pins. The fallback when there is no probe "
                    + "and no working USB.",
            },
        ];
}
