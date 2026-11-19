namespace NotchTasks.Core;

/// A POST of a JSON body to a local server. Returns the reply text, or null when there is none. The app's implementation
/// sets the headers it is given and gives up after the timeout. Callers treat a throw or a null as "no answer".
public interface IHttpPost
{
    Task<string?> PostAsync(string url, IReadOnlyDictionary<string, string> headers, string body, TimeSpan timeout);
}

/// A GET of a local server. Returns the reply text, or null when there is none (see IHttpPost).
public interface IHttpGet
{
    Task<string?> GetAsync(string url, TimeSpan timeout);
}
