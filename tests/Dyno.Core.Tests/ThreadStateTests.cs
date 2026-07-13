using Dyno.Core;
using Xunit;

namespace Dyno.Core.Tests;

public class ThreadStateTests
{
    // The firmware sends osThreadGetState() — CMSIS-RTOS2's osThreadState_t, not FreeRTOS's
    // eTaskState. The two disagree (eTaskState puts Running at 0, where CMSIS puts Inactive), so
    // these numbers are the contract; getting them from the wrong enum mislabels every row.
    [Theory]
    [InlineData(0, "Inactive")]
    [InlineData(1, "Ready")]
    [InlineData(2, "Running")]
    [InlineData(3, "Blocked")]
    [InlineData(4, "Terminated")]
    [InlineData(-1, "Error")]
    public void Labels_EveryCmsisThreadState(int state, string expected) =>
        Assert.Equal(expected, ThreadStateExtensions.ToLabel(state));

    [Fact]
    public void UnknownState_KeepsTheRawNumber_RatherThanGuessing()
    {
        // An unrecognised state means the device reports something this host does not know about.
        // Naming it anyway would hide that, so the number stays visible.
        Assert.Equal("? (7)", ThreadStateExtensions.ToLabel(7));
    }
}
