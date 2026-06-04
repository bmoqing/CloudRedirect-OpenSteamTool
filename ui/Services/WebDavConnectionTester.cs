using System;
using System.Net.Http.Headers;
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

        try
        {
            var baseUri = EnsureTrailingSlash(uri);
            var rootName = "cloudredirect-test-" + Guid.NewGuid().ToString("N");
            var rootUri = new Uri(baseUri, Uri.EscapeDataString(rootName) + "/");
            var nestedUri = new Uri(rootUri, "nested/");
            var emptyDirUri = new Uri(rootUri, "empty-dir/");
            var fileUri = new Uri(nestedUri, Uri.EscapeDataString("中文 文件.txt"));
            var conflictUri = new Uri(rootUri, "conflict.txt");
            var payload = "CloudRedirect WebDAV test " + Guid.NewGuid().ToString("N");
            var conflictPayload = "CloudRedirect WebDAV overwrite " + Guid.NewGuid().ToString("N");

            try
            {
                using var rootCheck = await SendAsync(client, "PROPFIND", baseUri, cancellationToken, Depth0Body(), "application/xml", ("Depth", "0"));
                if (!IsPropfindOk(rootCheck))
                    return new WebDavTestResult(false, Explain(rootCheck));

                using var mkRoot = await SendAsync(client, "MKCOL", rootUri, cancellationToken);
                if (!IsAllowed(mkRoot, HttpStatusCode.Created, HttpStatusCode.MethodNotAllowed))
                    return new WebDavTestResult(false, S.Format("CloudProvider_WebDavTestStepFailed", "MKCOL root", (int)mkRoot.StatusCode, mkRoot.ReasonPhrase ?? ""));

                using var mkNested = await SendAsync(client, "MKCOL", nestedUri, cancellationToken);
                if (!IsAllowed(mkNested, HttpStatusCode.Created, HttpStatusCode.MethodNotAllowed))
                    return new WebDavTestResult(false, S.Format("CloudProvider_WebDavTestStepFailed", "MKCOL nested", (int)mkNested.StatusCode, mkNested.ReasonPhrase ?? ""));

                using var mkEmpty = await SendAsync(client, "MKCOL", emptyDirUri, cancellationToken);
                if (!IsAllowed(mkEmpty, HttpStatusCode.Created, HttpStatusCode.MethodNotAllowed))
                    return new WebDavTestResult(false, S.Format("CloudProvider_WebDavTestStepFailed", "MKCOL empty", (int)mkEmpty.StatusCode, mkEmpty.ReasonPhrase ?? ""));

                using var put = await SendAsync(client, "PUT", fileUri, cancellationToken, payload, "text/plain");
                if (!IsAllowed(put, HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.NoContent))
                    return new WebDavTestResult(false, S.Format("CloudProvider_WebDavTestStepFailed", "PUT", (int)put.StatusCode, put.ReasonPhrase ?? ""));

                using (var get = await client.GetAsync(fileUri, cancellationToken))
                {
                    if (get.StatusCode != HttpStatusCode.OK)
                        return new WebDavTestResult(false, S.Format("CloudProvider_WebDavTestStepFailed", "GET", (int)get.StatusCode, get.ReasonPhrase ?? ""));
                    var text = await get.Content.ReadAsStringAsync(cancellationToken);
                    if (!string.Equals(text, payload, StringComparison.Ordinal))
                        return new WebDavTestResult(false, S.Get("CloudProvider_WebDavTestContentMismatch"));
                }

                using var putConflict1 = await SendAsync(client, "PUT", conflictUri, cancellationToken, "v1", "text/plain");
                if (!IsAllowed(putConflict1, HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.NoContent))
                    return new WebDavTestResult(false, S.Format("CloudProvider_WebDavTestStepFailed", "PUT conflict", (int)putConflict1.StatusCode, putConflict1.ReasonPhrase ?? ""));

                using var putConflict2 = await SendAsync(client, "PUT", conflictUri, cancellationToken, conflictPayload, "text/plain");
                if (!IsAllowed(putConflict2, HttpStatusCode.OK, HttpStatusCode.Created, HttpStatusCode.NoContent))
                    return new WebDavTestResult(false, S.Format("CloudProvider_WebDavTestStepFailed", "PUT overwrite", (int)putConflict2.StatusCode, putConflict2.ReasonPhrase ?? ""));

                using (var getConflict = await client.GetAsync(conflictUri, cancellationToken))
                {
                    if (getConflict.StatusCode != HttpStatusCode.OK)
                        return new WebDavTestResult(false, S.Format("CloudProvider_WebDavTestStepFailed", "GET overwrite", (int)getConflict.StatusCode, getConflict.ReasonPhrase ?? ""));
                    var text = await getConflict.Content.ReadAsStringAsync(cancellationToken);
                    if (!string.Equals(text, conflictPayload, StringComparison.Ordinal))
                        return new WebDavTestResult(false, S.Get("CloudProvider_WebDavTestContentMismatch"));
                }

                using var list = await SendAsync(client, "PROPFIND", rootUri, cancellationToken, Depth0Body(), "application/xml", ("Depth", "1"));
                if (!IsPropfindOk(list))
                    return new WebDavTestResult(false, S.Format("CloudProvider_WebDavTestStepFailed", "PROPFIND list", (int)list.StatusCode, list.ReasonPhrase ?? ""));

                return new WebDavTestResult(true, S.Get("CloudProvider_WebDavTestSuccessFull"));
            }
            finally
            {
                await TryDeleteAsync(client, fileUri, cancellationToken);
                await TryDeleteAsync(client, conflictUri, cancellationToken);
                await TryDeleteAsync(client, nestedUri, cancellationToken);
                await TryDeleteAsync(client, emptyDirUri, cancellationToken);
                await TryDeleteAsync(client, rootUri, cancellationToken);
            }
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

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        var text = uri.ToString();
        return text.EndsWith("/", StringComparison.Ordinal) ? uri : new Uri(text + "/");
    }

    private static string Depth0Body()
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/><d:getcontentlength/></d:prop></d:propfind>";

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string method,
        Uri uri,
        CancellationToken cancellationToken,
        string? body = null,
        string contentType = "text/plain",
        params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), uri);
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        if (body != null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static bool IsPropfindOk(HttpResponseMessage response)
        => response.StatusCode == HttpStatusCode.MultiStatus || response.IsSuccessStatusCode;

    private static bool IsAllowed(HttpResponseMessage response, params HttpStatusCode[] allowed)
        => Array.IndexOf(allowed, response.StatusCode) >= 0;

    private static string Explain(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => S.Get("CloudProvider_WebDavTestUnauthorized"),
            HttpStatusCode.Forbidden => S.Get("CloudProvider_WebDavTestForbidden"),
            HttpStatusCode.NotFound => S.Get("CloudProvider_WebDavTestNotFound"),
            HttpStatusCode.MethodNotAllowed => S.Get("CloudProvider_WebDavTestMethodNotAllowed"),
            _ => S.Format("CloudProvider_WebDavTestHttpError", code, response.ReasonPhrase ?? "")
        };
    }

    private static async Task TryDeleteAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
            using var _ = await client.SendAsync(request, cancellationToken);
        }
        catch
        {
            // Cleanup best-effort only.
        }
    }
}
