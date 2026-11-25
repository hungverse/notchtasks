namespace NotchTasks.Core.Sync;

/// Swift's Date as JSON: a Double of seconds since 2001-01-01T00:00:00Z (Swift's reference date), at millisecond precision.
/// Port of toSwiftDate / fromSwiftDate in electron/src/core/swiftjson.js.
public static class SwiftDate
{
    private const long SwiftEpochMs = 978307200000; // 1970-01-01 to 2001-01-01, in ms
    private const double MaxAbsMs = 9e15; // the largest integer a long holds exactly enough to be read here

    /// Seconds since 2001 for the moment (the millisecond part is kept, the rest is dropped).
    public static double From(DateTimeOffset date) => (date.ToUnixTimeMilliseconds() - SwiftEpochMs) / 1000.0;

    /// The moment for Swift's seconds since 2001, rounded to the millisecond as JavaScript's Math.round rounds it.
    /// Throws FormatException for a number that is not finite or is outside the calendar.
    public static DateTimeOffset To(double seconds)
    {
        if (!double.IsFinite(seconds))
        {
            throw new FormatException("the Swift date is not a finite number");
        }
        var ms = Math.Floor((seconds * 1000) + 0.5);
        if (Math.Abs(ms) > MaxAbsMs)
        {
            throw new FormatException("the Swift date is out of range");
        }
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)ms + SwiftEpochMs);
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw new FormatException("the Swift date is out of range", e);
        }
    }
}
