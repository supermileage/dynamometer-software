using Dyno.Core.Serial;
using Xunit;

namespace Dyno.Core.Tests;

/// <summary>
/// A board that reboots comes back on whatever ttyACM number is free, not the one it had, so a
/// reconnect that goes looking for the old node finds nothing. These cover the way back: the udev
/// symlink that names the board rather than the node it landed on.
///
/// Exercised against real symlinks in a temp directory rather than a mocked filesystem, because
/// what is actually in question is symlink resolution — the one part a fake would have to
/// reimplement to test at all. Linux-only for the same reason.
/// </summary>
public sealed class PortAliasTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dyno-alias-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Path(params string[] parts) => System.IO.Path.Combine([_root, .. parts]);

    /// <summary>A stand-in for a device node. Its contents are irrelevant — only that something is
    /// there for a link to resolve to, and that deleting it makes the link dangle the way an
    /// unplugged board does.</summary>
    private string Node(string name)
    {
        var path = Path(name);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private string Link(string name, string target)
    {
        var path = Path(name);
        File.CreateSymbolicLink(path, target);
        return path;
    }

    [Fact]
    public void FindsTheLinkPointingAtTheNode()
    {
        var node = Node("ttyACM0");
        var alias = Link("dyno", node);

        var found = PortAlias.For(node, alias);

        Assert.NotNull(found);
        Assert.Equal(alias, found.Path);
    }

    [Fact]
    public void IgnoresLinksToOtherDevices()
    {
        var node = Node("ttyACM0");
        Node("ttyACM1");
        var other = Link("other", Path("ttyACM1"));

        Assert.Null(PortAlias.For(node, other));
    }

    [Fact]
    public void SearchesEveryLinkInADirectorySource()
    {
        var byId = Directory.CreateDirectory(Path("by-id")).FullName;
        var node = Node("ttyACM0");
        Node("ttyACM1");
        File.CreateSymbolicLink(System.IO.Path.Combine(byId, "usb-other-if00"), Path("ttyACM1"));
        File.CreateSymbolicLink(System.IO.Path.Combine(byId, "usb-dyno-if00"), node);

        var found = PortAlias.For(node, byId);

        Assert.Equal(System.IO.Path.Combine(byId, "usb-dyno-if00"), found?.Path);
    }

    [Fact]
    public void PrefersTheEarlierSource()
    {
        var node = Node("ttyACM0");
        var byId = Link("by-id-link", node);
        var project = Link("dyno", node);

        // by-id carries the serial number and so tells two identical boards apart; /dev/dyno
        // matches on VID/PID alone. When both point at the board, the discriminating one wins.
        Assert.Equal(byId, PortAlias.For(node, byId, project)?.Path);
    }

    [Fact]
    public void NeverReturnsTheNodeItself()
    {
        var node = Node("ttyACM0");

        // The node trivially resolves to itself, and is exactly the name that does not survive a
        // re-enumeration — returning it would look like success and follow nothing.
        Assert.Null(PortAlias.For(node, node));
    }

    [Fact]
    public void ReturnsNothingWhenNoLinkExists()
    {
        var node = Node("ttyACM0");

        Assert.Null(PortAlias.For(node, Path("absent"), Path("also-absent")));
    }

    [Fact]
    public void ReturnsNothingForANodeThatIsNotThere()
    {
        Assert.Null(PortAlias.For(Path("ttyACM9"), Path("anything")));
    }

    [Fact]
    public void ResolvesToWhicheverNodeTheBoardCameBackAs()
    {
        var node = Node("ttyACM0");
        var alias = Link("dyno", node);
        var found = PortAlias.For(node, alias);
        Assert.Equal(node, found?.CurrentNode());

        // The board reboots: the old node goes, and udev re-points the link at the new one.
        File.Delete(alias);
        File.Delete(node);
        var moved = Node("ttyACM1");
        Link("dyno", moved);

        Assert.Equal(moved, found?.CurrentNode());
    }

    [Fact]
    public void ResolvesToNothingWhileTheBoardIsOffTheBus()
    {
        var node = Node("ttyACM0");
        var found = PortAlias.For(node, Link("dyno", node));

        // A dangling link is precisely how a removed device presents, and is what tells the
        // reconnect watcher to keep waiting rather than to try opening a port that is not there.
        File.Delete(node);

        Assert.Null(found?.CurrentNode());
    }
}
