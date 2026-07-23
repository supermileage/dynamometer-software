using Dyno.Core.Messages;
using Xunit;

namespace Dyno.Core.Tests;

/// <summary>
/// The catalog and the fault enums are rendered from one schema by two different generators —
/// error_msg_generate.py and generate.py — so holding them against each other is a real check on
/// both, not a tautology. What it guards is narrow and concrete: a fault reaching the event log as
/// a bare number because someone added it to the schema and nothing downstream noticed.
/// </summary>
public class ErrorCatalogTests
{
    /// <summary>Every <c>*_error_ids</c> member in the generated contract, as
    /// (enum type, member name, task-local value).</summary>
    private static IEnumerable<(Type Enum, string Member, uint Value)> FirmwareFaults()
    {
        var faultEnums = typeof(MessageConstants)
            .Assembly.GetTypes()
            .Where(t => t.IsEnum && t.Name.EndsWith("error_ids", StringComparison.Ordinal));

        foreach (var faults in faultEnums)
        {
            foreach (var member in Enum.GetNames(faults))
            {
                var value = (uint)Convert.ChangeType(Enum.Parse(faults, member), typeof(uint))!;
                yield return (faults, member, value);
            }
        }
    }

    /// <summary>The catalog keyed by the schema name of each fault — the severity prefix the
    /// generator strips off, put back on.</summary>
    private static Dictionary<string, ErrorMessageDef> ByMemberName() =>
        ErrorCatalog.All.ToDictionary(f => (f.IsWarning ? "WARNING_" : "ERROR_") + f.Name);

    public static TheoryData<string, uint> EveryFirmwareFault()
    {
        var data = new TheoryData<string, uint>();
        foreach (var (_, member, value) in FirmwareFaults())
        {
            data.Add(member, value);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryFirmwareFault))]
    public void EveryFaultTheFirmwareDefinesHasACatalogEntry(string member, uint value)
    {
        var catalog = ByMemberName();

        Assert.True(catalog.ContainsKey(member), $"{member} has no ErrorCatalog entry");
        // The entry's packed code must carry that member's own value in its low bits, or the
        // catalog would answer a code no board ever sends.
        Assert.Equal(value, catalog[member].Code & ~MessageConstants.TASK_OFFSET_MASK);
        Assert.NotEqual(string.Empty, catalog[member].Description);
    }

    [Fact]
    public void EveryCatalogEntryNamesAFaultTheFirmwareDefines()
    {
        var firmware = FirmwareFaults().Select(f => f.Member).ToHashSet();

        Assert.Empty(ByMemberName().Keys.Where(name => !firmware.Contains(name)));
    }

    [Fact]
    public void CodesAreUnique()
    {
        // Find() is a dictionary keyed on Code, so a duplicate is a generator bug that would
        // throw at type-initialization time rather than fail here — but it would throw inside a
        // TypeInitializationException on first use, in the app, at runtime. Say it plainly instead.
        Assert.Empty(ErrorCatalog.All.GroupBy(f => f.Code).Where(g => g.Count() > 1));
    }

    /// <summary>A fault enum's <c>task:</c> is written by hand in the schema next to an enum
    /// named for the same task, so the failure mode is a copy-paste that attributes one task's
    /// faults to another. The names have to agree for that not to have happened: one is a prefix
    /// of the other (bpm_task_error_ids belongs to BPM_CONTROLLER).</summary>
    [Fact]
    public void EachFaultIsAttributedToTheTaskItsEnumIsNamedFor()
    {
        var catalog = ByMemberName();

        foreach (var (faults, member, _) in FirmwareFaults())
        {
            var stem = faults
                .Name.Replace("_task_error_ids", "")
                .Replace("_error_ids", "")
                .ToUpperInvariant();
            var task = catalog[member].Task.ToString().Replace("TASK_OFFSET_", string.Empty);

            Assert.True(
                stem.StartsWith(task, StringComparison.Ordinal)
                    || task.StartsWith(stem, StringComparison.Ordinal),
                $"{faults.Name} is attributed to {task}; check its `task:` in the schema"
            );
        }
    }

    [Fact]
    public void Find_ReturnsTheDescriptionForAKnownCode()
    {
        uint code =
            (uint)task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADS1115
            | (uint)force_sensor_ads1115_error_ids.ERROR_FORCE_SENSOR_ADS1115_INIT_FAILURE;

        var fault = Assert.NotNull(ErrorCatalog.Find(code));

        Assert.Equal("FORCE_SENSOR_ADS1115_INIT_FAILURE", fault.Name);
        Assert.False(fault.IsWarning);
        Assert.Contains("I2C", fault.Description);
    }

    [Fact]
    public void Find_IsNullForACodeThisBuildDoesNotKnow()
    {
        // A board running newer firmware than the app: the number is real, the meaning is not here.
        Assert.Null(ErrorCatalog.Find((uint)task_offset_t.TASK_OFFSET_BPM_CONTROLLER | 4999u));
    }
}
