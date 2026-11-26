using System.Security;
using System.Text;

namespace NotchTasks.Services.Notifications;

/// The XML of a Windows toast: the title and the subtitle as text, and no sound, because the app plays its own sounds (the Mac's
/// notifications are silent too). Both texts are escaped, so a title with markup or quotes stays text, and control characters
/// that XML cannot hold are dropped, since one of them would make the whole document invalid and the toast would fail.
public static class ToastXml
{
    /// The toast's XML. An empty subtitle leaves the second line out.
    public static string Build(string title, string subtitle)
    {
        var xml = new StringBuilder("<toast><visual><binding template=\"ToastGeneric\">");
        xml.Append("<text>").Append(Escape(title)).Append("</text>");
        if (subtitle.Length > 0)
        {
            xml.Append("<text>").Append(Escape(subtitle)).Append("</text>");
        }
        xml.Append("</binding></visual><audio silent=\"true\"/></toast>");
        return xml.ToString();
    }

    /// The text as XML character data: the characters XML cannot hold are removed first, then markup is escaped.
    private static string Escape(string text) => SecurityElement.Escape(WithoutInvalidCharacters(text)) ?? string.Empty;

    /// XML 1.0 holds tab, line feed, carriage return and everything from space up, except the two non-characters.
    private static string WithoutInvalidCharacters(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var valid = c is '\t' or '\n' or '\r' || (c >= ' ' && c is not '￾' and not '￿');
            if (valid)
            {
                clean.Append(c);
            }
        }
        return clean.ToString();
    }
}
