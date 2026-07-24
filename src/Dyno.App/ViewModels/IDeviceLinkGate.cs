namespace Dyno.App.ViewModels;

/// <summary>
/// The device link as the Firmware page needs to see it: something to get out of the way before a
/// programming tool can have the board, and to put back once it has finished.
/// </summary>
/// <remarks>
/// Every flash method here reaches the board over the same USB the link is holding open, and a
/// board being programmed resets at the end of it. Flashing underneath a live link therefore
/// destroys it either way — the port stops answering mid-run, and the node the link is bound to
/// stops existing. Doing it deliberately, in a known order, is the difference between a reconnect
/// and a stuck link the user has to unpick by hand.
/// </remarks>
public interface IDeviceLinkGate
{
    bool IsConnected { get; }

    /// <summary>Releases the port and stops any reconnect already in progress. Returns whether
    /// there was in fact a link to release, which is the caller's cue to put one back.</summary>
    Task<bool> SuspendAsync();

    /// <summary>Waits for the board to re-appear and links to it again, following it if it comes
    /// back under a different device node. Gives up after its own timeout rather than waiting on a
    /// board that is not coming back, and returns rather than throwing when cancelled — for the
    /// caller, giving up early and giving up late are the same outcome.</summary>
    Task ResumeAsync(CancellationToken cancellationToken = default);
}
