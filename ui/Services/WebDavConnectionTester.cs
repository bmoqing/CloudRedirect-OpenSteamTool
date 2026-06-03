using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CloudRedirect.Resources;

namespace CloudRedirect.Services;

internal sealed record WebDavTestResult(bool Success, string Message);

internal static class WebDavConnectionTester
{
    public static async Task<WebDavTestResult> TestAsync(
        string url,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new WebDavTestResult(false, S.Get("CloudProvider_WebDavRequiresHttps"));
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return new WebDavTestResult(false, S.Get("CloudProvider_WebDavNoQueryFragment"));

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return new WebDavTestResult(false, S.Get("CloudProvider_WebDavMissingCredentials"));

        using var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(username, password),
            PreAuthenticate = true,
        };
        handler.AllowAutoRedirect = true;

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CloudRedirect-WebDAV-Test/1.0");

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), uri);
        request.Headers.TryAddWithoutValidation("Depth", "0");
        request.Content = new StringContent(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/></d:prop></d:propfind>",
            Encoding.UTF8,
            "application/xml");

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var code = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.MultiStatus || response.IsSuccessStatusCode)
                return new WebDavTestResult(true, S.Format("CloudProvider_WebDavTestSuccess", code));

            var message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => S.Get("CloudProvider_WebDavTestUnauthorized"),
                HttpStatusCode.Forbidden => S.Get("CloudProvider_WebDavTestForbidden"),
                HttpStatusCode.NotFound => S.Get("CloudProvider_WebDavTestNotFound"),
                HttpStatusCode.MethodNotAllowed => S.Get("CloudProvider_WebDavTestMethodNotAllowed"),
                _ => S.Format("CloudProvider_WebDavTestHttpError", code, response.ReasonPhrase ?? "")
            };
            return new WebDavTestResult(false, message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WebDavTestResult(false, S.Get("CloudProvider_WebDavTestTimeout"));
        }
        catch (HttpRequestException ex)
        {
            return new WebDavTestResult(false, S.Format("CloudProvider_WebDavTestNetworkError", ex.Message));
        }
    }
}
