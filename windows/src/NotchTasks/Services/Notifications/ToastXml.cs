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

    /// XML 1.0 holds tab, line feed, carriage return and everything from space up, except the two non-characters and the
    /// surrogates. A surrogate pair (an emoji) is kept; a lone surrogate is dropped, since XmlDocument rejects it.
    private static string WithoutInvalidCharacters(string text)
    {
        var clean = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                clean.Append(c).Append(text[i + 1]);
                i++;
            }
            else if (c is '\t' or '\n' or '\r' || (c >= ' ' && c is not '￾' and not '￿' && !char.IsSurrogate(c)))
            {
                clean.Append(c);
            }
        }
        return clean.ToString();
    }
}
