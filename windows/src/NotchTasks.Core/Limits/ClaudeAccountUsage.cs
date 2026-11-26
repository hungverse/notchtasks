using System.Globalization;
using System.Text.Json;

namespace NotchTasks.Core.Limits;

/// Claude's plan usage for the signed-in account: the sign-in token Claude keeps in its credentials file, and the reply of the
/// usage endpoint. Pure: no file and no network here (the app's fetcher does both). The reply's shape is not documented, so it is
/// read tolerantly.
public static class ClaudeAccountUsage
{
    /// The usage endpoint. The token goes in the Authorization header of a GET to this address and nowhere else.
    public const string Url = "https://api.anthropic.com/api/oauth/usage";

    /// The access token of the credentials file's claudeAiOauth, when it is present, non-empty and not expired at now (expiresAt is
    /// unix milliseconds; equal to now counts as expired). Null otherwise, including for a file that is not JSON. The token is
    /// never refreshed here.
    public static string? ReadToken(string json, DateTimeOffset now)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var oauth = Get(doc.RootElement, "claudeAiOauth");
            if (oauth.ValueKind != JsonValueKind.Object) return null;

            var token = Get(oauth, "accessToken");
            if (token.ValueKind != JsonValueKind.String) return null;
            var text = token.GetString();
            if (string.IsNullOrWhiteSpace(text)) return null;

            var expires = Get(oauth, "expiresAt");
            if (expires.ValueKind != JsonValueKind.Number || !expires.TryGetDouble(out var expiresMs)) return null;
            if (expiresMs <= now.ToUnixTimeMilliseconds()) return null;

            return text;
        }
    }

    /// The 5h ("five_hour") and 7d ("seven_day") windows of the usage reply. A window that is null or missing is skipped, and so is
    /// one whose utilization is not a number. A resets_at that is null or does not parse is 5 h (or 7 d) from now. Other keys are
    /// ignored. A reply that is not JSON gives none.
    public static IReadOnlyList<LimitWindow> Parse(string json, DateTimeOffset now)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Array.Empty<LimitWindow>();
        }

        using (doc)
        {
            var list = new List<LimitWindow>();
            foreach (var (key, label, span) in new[]
                     {
                         ("five_hour", "5h", TimeSpan.FromHours(5)),
                         ("seven_day", "7d", TimeSpan.FromDays(7)),
                     })
            {
                var w = Get(doc.RootElement, key);
                if (w.ValueKind != JsonValueKind.Object) continue;

                var utilization = Get(w, "utilization");
                if (utilization.ValueKind != JsonValueKind.Number || !utilization.TryGetDouble(out var used)) continue;

                var resets = now + span;
                var at = Get(w, "resets_at");
                if (at.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    resets = parsed;
                }
                list.Add(new LimitWindow(label, used, resets));
            }
            return list;
        }
    }

    private static JsonElement Get(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
}
