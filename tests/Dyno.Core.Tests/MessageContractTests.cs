using System.Runtime.InteropServices;
using Dyno.Core.Messages;
using Xunit;

namespace Dyno.Core.Tests;

public class MessageContractTests
{
    [Fact]
    public void ExpectedSizes_AreGenerated()
    {
        Assert.NotEmpty(MessageContract.ExpectedSizes);
    }

    [Fact]
    public void EveryType_MatchesItsSchemaAssertedSize()
    {
        foreach (var (type, expected) in MessageContract.ExpectedSizes)
        {
            int actual = type.IsEnum
                ? Marshal.SizeOf(Enum.GetUnderlyingType(type))
                : Marshal.SizeOf(type);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Header_Is12Bytes()
    {
        Assert.Equal(12, Marshal.SizeOf<usb_msg_header_t>());
    }
}
