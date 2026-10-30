using System.Net;

namespace Sojourn.Session.Media;

/// <summary>
/// What kind of media a URL is, asked of its server before a player is chosen (the viewer's mimeDiscoveryCoro): a HEAD,
/// or a one-byte GET where HEAD isn't answered, following redirects by hand so every hop meets the URL rules (no
/// private addresses unless the grid is local). Runs only for media about to play: asking is already telling the
/// server you're there. No cookies, no credentials.
/// </summary>
static class MimeProbe
{
    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        UseProxy = true,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        AutomaticDecompression = DecompressionMethods.None,
    })
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Sojourn/0.1" } },
    };

    public static async Task<(MediaKind Kind, string? Mime)> RunAsync(string url, bool privateOk)
    {
        string at = url;
        for (int hop = 0; hop < 6; hop++)
        {
            if (MediaUrls.WebRefusal(at, privateOk) != null) break;
            HttpResponseMessage? r = null;
            try
            {
                using (var head = new HttpRequestMessage(HttpMethod.Head, at)) r = await Http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead);
                if ((int)r.StatusCode is 405 or 403 or 501 or 400)
                {
                    r.Dispose();
                    using var get = new HttpRequestMessage(HttpMethod.Get, at);
                    get.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                    r = await Http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead);
                }
                if ((int)r.StatusCode is >= 300 and < 400 && r.Headers.Location is { } loc)
                {
                    at = new Uri(new Uri(at), loc).ToString();
                    continue;
                }
                var mime = r.Content.Headers.ContentType?.MediaType;
                return (MediaUrls.KindOf(at, mime), mime);
            }
            catch { break; }
            finally { r?.Dispose(); }
        }
        // no answer: by the file's extension, else a page (the browser has its own go at it)
        return (MediaUrls.KindOf(url, null), null);
    }
}
