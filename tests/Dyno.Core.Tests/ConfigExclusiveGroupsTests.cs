using Dyno.Core.Firmware;
using Xunit;

namespace Dyno.Core.Tests;

public class ConfigExclusiveGroupsTests
{
    [Theory]
    [InlineData("LUMEX_LCD_TASK_ENABLE", "ILI9341_LCD_TASK_ENABLE")]
    [InlineData("ILI9341_LCD_TASK_ENABLE", "LUMEX_LCD_TASK_ENABLE")]
    public void EachDisplayPanelExcludesTheOther(string turnedOn, string expectedOff)
    {
        Assert.Equal([expectedOff], ConfigExclusiveGroups.Siblings(turnedOn));
    }

    [Fact]
    public void SettingsInNoGroupExcludeNothing()
    {
        Assert.Empty(ConfigExclusiveGroups.Siblings("PID_CONTROLLER_TASK_ENABLE"));
        Assert.Empty(ConfigExclusiveGroups.Siblings(""));
    }

    // Exclusion is a relation between members, so a one-member group is a typo that would silently
    // do nothing rather than fail.
    [Fact]
    public void EveryGroupHasSomethingToExclude()
    {
        Assert.All(ConfigExclusiveGroups.Groups, group => Assert.True(group.Count >= 2));
    }

    // A name in two groups would make "turn the others off" depend on which group was consulted.
    [Fact]
    public void NoSettingBelongsToTwoGroups()
    {
        var all = ConfigExclusiveGroups.Groups.SelectMany(g => g).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
    }
}
