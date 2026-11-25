using System.Globalization;

namespace NotchTasks.Core.Updates;

/// Where the footer's update line stands. The same table as Updater.footerState on the Mac.
public enum FooterState { Current, Checking, UpToDate, Offered, Failed }

/// The update rules of Updater.swift and electron/src/core/updater.js as pure functions. The network is in UpdateClient.
public static class UpdateRules
{
    /// An update URL is https, or plain http to 127.0.0.1 (tests and local feeds). Anything else, or no absolute URL, is refused.
    public static bool IsAllowedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return false;
        }
        return u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.Host == "127.0.0.1");
    }

    /// "10.0.22631" becomes [10, 0, 22631]. Null unless there are at most three parts and each is a plain number.
    public static int[]? ParseOS(string text)
    {
        var parts = text.Split('.');
        if (parts.Length > 3)
        {
            return null;
        }
        var values = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]))
            {
                return null;
            }
        }
        return values;
    }

    /// -1, 0 or 1. A missing part counts as 0, so "10.0" equals "10.0.0" (the Mac's OSVersion order).
    public static int CompareOS(int[] a, int[] b)
    {
        for (var i = 0; i < 3; i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y)
            {
                return x < y ? -1 : 1;
            }
        }
        return 0;
    }

    /// A feed build is offered only when it is higher than the running one.
    public static bool IsNewer(int build, int current) => build > current;

    /// The update notice is shown once per build: a build other than the last one shown.
    public static bool ShouldAnnounce(int build, int shown) => build != shown;

    /// The build number of a version: major * 10000 + minor * 100 + the third part, the way the feed numbers its builds.
    public static int BuildOf(Version v) => v.Major * 10000 + v.Minor * 100 + Math.Max(v.Build, 0);

    /// The footer's update state. Installing wins, then a failure with an offer, then an offer, then a check in progress;
    /// after a manual check that found nothing it is "up to date".
    public static FooterState Footer(bool checking, bool installing, bool offered, bool error, bool checkedManually)
    {
        if (installing)
        {
            return FooterState.Offered;
        }
        if (error && offered)
        {
            return FooterState.Failed;
        }
        if (offered)
        {
            return FooterState.Offered;
        }
        if (checking)
        {
            return FooterState.Checking;
        }
        return checkedManually ? FooterState.UpToDate : FooterState.Current;
    }
}
