using Dyno.Core.Messages;

namespace Dyno.Core.SysConfig;

/// <summary>
/// What the running device is believed to hold in its sysconfig store, and therefore what still has
/// to be written to it.
/// </summary>
/// <remarks>
/// The board's store is plain RAM — there is no flash behind it — so it holds the config.h defaults
/// from the moment it boots and keeps whatever the host last wrote only for as long as it stays
/// powered. The host is the one that remembers (SQLite), which makes "what does the device
/// currently have?" a question only the host can answer, and only by tracking what it has sent.
///
/// That belief is what <see cref="Outstanding"/> subtracts from the values the user wants, so one
/// code path serves both jobs: after <see cref="Forget"/> (a fresh or possibly-rebooted board) it
/// yields the entire catalog, defaults included, and after a save it yields only the parameter that
/// actually changed. A write that is never confirmed simply stays outstanding and goes out again on
/// the next pass.
/// </remarks>
public sealed class SysConfigDeviceMirror
{
    private readonly Dictionary<sysconfig_param_t, double> _applied = new();

    /// <summary>Everything this device was believed to hold, unbelieved. Call it on every handshake:
    /// a link that dropped and came back may have done so *because* the board rebooted, and a
    /// rebooted board is back on its defaults with no way to tell us so.</summary>
    public void Forget() => _applied.Clear();

    /// <summary>Records that the device acked a write of <paramref name="value"/>.</summary>
    public void Confirm(sysconfig_param_t id, double value) => _applied[id] = value;

    /// <summary>How many parameters the device is currently believed to hold as written.</summary>
    public int ConfirmedCount => _applied.Count;

    /// <summary>
    /// Every parameter, in catalog order, whose wanted value the device is not known to already
    /// hold. <paramref name="wanted"/> is keyed by wire id; a parameter absent from it is one the
    /// user never overrode, so its wanted value is the firmware default — which still has to be
    /// *sent* to a board that might be holding a previous session's value for it.
    /// </summary>
    public IReadOnlyList<(SysConfigParameterDef Def, double Value)> Outstanding(
        IReadOnlyDictionary<sysconfig_param_t, double> wanted
    )
    {
        var outstanding = new List<(SysConfigParameterDef, double)>();
        foreach (var def in SysConfigCatalog.Parameters)
        {
            var value = wanted.TryGetValue(def.Id, out var v) ? v : def.Default;
            if (!_applied.TryGetValue(def.Id, out var applied) || applied != value)
            {
                outstanding.Add((def, value));
            }
        }
        return outstanding;
    }
}
