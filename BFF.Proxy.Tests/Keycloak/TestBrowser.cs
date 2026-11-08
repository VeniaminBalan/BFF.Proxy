using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using BFF.Proxy.Tests.Integration.Infrastructure;

namespace BFF.Proxy.Tests.Keycloak;

// Just enough of a browser to drive the real login: a cookie jar, redirect following, HTML form submission and
// the auto-submitting form Keycloak uses for response_mode=form_post (the one bit of JavaScript it needs).
//
// Cookies: ASP.NET Core marks the OIDC correlation/nonce cookies Secure even on http://localhost, which browsers
// accept for localhost but CookieContainer would never send back over http. The jar therefore ignores Secure.
public sealed partial class TestBrowser : IDisposable
{
    private static readonly HtmlParser Parser = new();
    private readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });

    public CookieContainer Cookies { get; } = new();
    public List<Uri> Visited { get; } = [];

    public Task<BrowserPage> NavigateAsync(string url) => NavigateAsync(new HttpRequestMessage(HttpMethod.Get, url));

    // Sends the request and follows redirects and auto-submitting forms, like a top-level navigation.
    public async Task<BrowserPage> NavigateAsync(HttpRequestMessage request)
    {
        for (var hop = 0; hop < 20; hop++)
        {
            using var response = await SendAsync(request);
            var url = request.RequestUri!;

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, location));
                continue;
            }

            var page = await BrowserPage.CreateAsync(url, response);
            if (page.AutoSubmitForm is { } form)
            {
                request = page.CreateSubmission(form, new Dictionary<string, string>());
                continue;
            }

            return page;
        }

        throw new InvalidOperationException($"Too many redirects; visited: {string.Join(" -> ", Visited)}");
    }

    // A single request (no redirect following), e.g. an XHR from the SPA.
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        var url = request.RequestUri!;
        var cookieHeader = Cookies.GetCookieHeader(url);
        if (!string.IsNullOrEmpty(cookieHeader))
            request.Headers.Add("Cookie", cookieHeader);

        var response = await _http.SendAsync(request);
        Visited.Add(url);

        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var setCookie in setCookies)
                Cookies.SetCookies(url, SecureAttribute().Replace(setCookie, ""));
        }

        return response;
    }

    public Task<BrowserPage> SubmitFormAsync(BrowserPage page, IHtmlFormElement form, IDictionary<string, string> values) =>
        NavigateAsync(page.CreateSubmission(form, values));

    public async Task<MeResponse?> GetMeAsync(Uri bff)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(bff, "/bff/me")));
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return null;
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "/bff/me");
        return await response.Content.ReadFromJsonAsync<MeResponse>(JsonSerializerOptions.Web);
    }

    public void ClearCookies(Uri site)
    {
        foreach (Cookie cookie in Cookies.GetCookies(site))
            cookie.Expired = true;
    }

    public void Dispose() => _http.Dispose();

    [GeneratedRegex(@";\s*secure(?=\s*(;|$))", RegexOptions.IgnoreCase)]
    private static partial Regex SecureAttribute();

    public sealed class BrowserPage
    {
        private BrowserPage(Uri url, HttpStatusCode status, string body, IHtmlDocument? document)
        {
            Url = url;
            Status = status;
            Body = body;
            Document = document;
        }

        public Uri Url { get; }
        public HttpStatusCode Status { get; }
        public string Body { get; }
        public IHtmlDocument? Document { get; }

        public IHtmlFormElement? LoginForm =>
            Document?.Forms.FirstOrDefault(f => f.QuerySelector("input[type=password]") is not null);

        // Keycloak's form_post response: <body onload="javascript:document.forms[0].submit()">
        public IHtmlFormElement? AutoSubmitForm =>
            Document?.Body?.GetAttribute("onload")?.Contains("submit()") == true ? Document.Forms.FirstOrDefault() : null;

        public static async Task<BrowserPage> CreateAsync(Uri url, HttpResponseMessage response)
        {
            var body = await response.Content.ReadAsStringAsync();
            var isHtml = response.Content.Headers.ContentType?.MediaType == "text/html";
            return new BrowserPage(url, response.StatusCode, body, isHtml ? Parser.ParseDocument(body) : null);
        }

        public HttpRequestMessage CreateSubmission(IHtmlFormElement form, IDictionary<string, string> values)
        {
            var fields = new Dictionary<string, string>();
            foreach (var input in form.Elements.OfType<IHtmlInputElement>())
            {
                if (string.IsNullOrEmpty(input.Name) || input.Type is "submit" or "button" or "image")
                    continue;
                if (input.Type is "checkbox" or "radio" && !input.IsChecked)
                    continue;
                fields[input.Name] = input.Value;
            }
            foreach (var (name, value) in values)
                fields[name] = value;

            var action = new Uri(Url, form.GetAttribute("action") ?? Url.ToString());
            return string.Equals(form.Method, "post", StringComparison.OrdinalIgnoreCase)
                ? new HttpRequestMessage(HttpMethod.Post, action) { Content = new FormUrlEncodedContent(fields) }
                : new HttpRequestMessage(HttpMethod.Get, new UriBuilder(action) { Query = new FormUrlEncodedContent(fields).ReadAsStringAsync().Result }.Uri);
        }

        public override string ToString() => $"{(int)Status} {Url}: {Body[..Math.Min(Body.Length, 300)]}";
    }
}
