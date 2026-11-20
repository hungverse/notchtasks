using NotchTasks.Core.Devices;
using Xunit;

namespace NotchTasks.Core.Tests.Devices;

/// A container with an empty kind is read again only while its device nodes have no class yet, or within 30 s of its Added event. A
/// container whose nodes have classes and still give no kind is read no more (a read per minute of nothing is churn).
public class DeviceCacheRecheckTests
{
    [Fact]
    public void EmptyKind_NodesHaveNoClassYet_Reclassified()
    {
        var known = new DeviceFact("{x}", "Unknown", Array.Empty<string>(), null);

        Assert.True(DeviceCache.NeedsClassify(known));
        Assert.True(DeviceCache.NeedsClassify(known, addedRecently: false));
    }

    [Fact]
    public void EmptyKind_NodesHaveClasses_NotAddedRecently_NotReclassified()
    {
        var known = new DeviceFact("{x}", "Unknown", Array.Empty<string>(), null) { HasClassNode = true };

        Assert.False(DeviceCache.NeedsClassify(known, addedRecently: false));
    }

    [Fact]
    public void EmptyKind_NodesHaveClasses_AddedRecently_Reclassified()
    {
        var known = new DeviceFact("{x}", "Unknown", Array.Empty<string>(), null) { HasClassNode = true };

        Assert.True(DeviceCache.NeedsClassify(known, addedRecently: true));
    }

    [Fact]
    public void KnownKind_NeverReclassified_EvenAddedRecently()
    {
        var known = new DeviceFact("{kb}", "USB Keyboard", new[] { "Input.Keyboard" }, 80) { HasClassNode = true };

        Assert.False(DeviceCache.NeedsClassify(known, addedRecently: true));
    }

    [Fact]
    public void NoKnownFact_AlwaysClassified()
    {
        Assert.True(DeviceCache.NeedsClassify(null));
        Assert.True(DeviceCache.NeedsClassify(null, addedRecently: false));
    }
}
