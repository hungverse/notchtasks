using NotchTasks.Core.Sensors;
using Xunit;

namespace NotchTasks.Core.Tests;

/// The stop event name is one Global name per user, so the app, the elevated installer and the SYSTEM helper all reach it.
public sealed class SensorStopNameTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    [Fact]
    public void StopEventName_IsTheGlobalNameForTheUser()
    {
        Assert.Equal(@"Global\NotchTasks.Sensors.Stop." + Sid, SensorNames.StopEventName(Sid));
    }

    [Fact]
    public void PipeName_StaysSessionLocalFormat()
    {
        // Pipes are not namespaced like events: the pipe keeps its own name.
        Assert.Equal("NotchTasks.Sensors." + Sid, SensorNames.PipeName(Sid));
    }
}
