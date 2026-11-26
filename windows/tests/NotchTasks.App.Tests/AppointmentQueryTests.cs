using NotchTasks.Services.Appointments;
using Windows.ApplicationModel.Appointments;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class AppointmentQueryTests
{
    [Fact]
    public void Options_FetchEveryPropertyTheParserReads()
    {
        var options = AppointmentQuery.Create();

        foreach (var property in new[]
                 {
                     AppointmentProperties.Subject,
                     AppointmentProperties.StartTime,
                     AppointmentProperties.Duration,
                     AppointmentProperties.AllDay,
                     AppointmentProperties.Location,
                     AppointmentProperties.Details,
                     AppointmentProperties.Uri,
                     AppointmentProperties.OnlineMeetingLink,
                 })
        {
            Assert.Contains(property, options.FetchProperties);
        }
    }

    [Fact]
    public void Options_CapTheCountAndSkipHiddenAppointments()
    {
        var options = AppointmentQuery.Create();

        Assert.Equal(50u, options.MaxCount);
        Assert.False(options.IncludeHidden);
    }
}
