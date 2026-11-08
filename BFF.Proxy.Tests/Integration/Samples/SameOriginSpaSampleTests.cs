using System.Net;
using BFF.Proxy.Tests.Integration.Infrastructure;

namespace BFF.Proxy.Tests.Integration.Samples;

// samples/static-spa:            COPY dist/ ./wwwroot/
// samples/build-from-source-spa: Node stage builds app/src -> dist (build.js is a plain copy), then COPY dist/ ./wwwroot/
[TestFixture("static-spa/dist")]
[TestFixture("build-from-source-spa/app/src")]
public class SameOriginSpaSampleTests(string spaOutputDir) : BffIntegrationTest
{
    private string _indexHtml = null!;

    protected override string CreateContentRoot()
    {
        _indexHtml = SampleLayout.ReadSampleFile(Path.Combine(spaOutputDir, "index.html"));
        var layout = DisposeAfterFixture(SampleLayout.Create()).CopyDirectory(spaOutputDir, "wwwroot");
        return layout.ContentRoot;
    }

    private async Task<(HttpStatusCode Status, string Body, string? ContentType)> GetAsync(string path, BffClient? client = null)
    {
        using var response = await (client ?? AnonymousClient()).Http.GetAsync(path);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response.Content.Headers.ContentType?.MediaType);
    }

    [TestCase("/")]
    [TestCase("/orders")]
    [TestCase("/orders/42/lines")]
    public async Task Root_and_client_side_routes_serve_the_spa_shell(string path)
    {
        var (status, body, contentType) = await GetAsync(path);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(contentType, Is.EqualTo("text/html"));
            Assert.That(body, Is.EqualTo(_indexHtml));
        });
    }

    [Test]
    public async Task Static_files_are_served_as_files()
    {
        var (status, body, _) = await GetAsync("/index.html");

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body, Is.EqualTo(_indexHtml));
        });
    }

    [Test]
    public async Task Missing_asset_is_a_404_not_the_spa_shell()
    {
        // Otherwise a broken <script src> would get HTML back and fail with a confusing parse error.
        var (status, _, _) = await GetAsync("/assets/missing.js");

        Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [TestCase("/bff/does-not-exist")]
    [TestCase("/health/does-not-exist")]
    public async Task Reserved_prefixes_are_never_answered_with_the_spa_shell(string path)
    {
        var (status, body, _) = await GetAsync(path);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(body, Is.Not.EqualTo(_indexHtml));
        });
    }

    [Test]
    public async Task Bff_endpoints_are_unaffected_by_the_spa()
    {
        var (status, _, _) = await GetAsync("/bff/me");

        Assert.That(status, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Api_routes_still_go_to_the_backend()
    {
        var client = await SignedInClientAsync();

        var (status, body, _) = await GetAsync("/api/orders", client);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body, Is.EqualTo("backend-ok"));
        });
    }

    [Test]
    public async Task Anonymous_api_call_is_challenged_not_served_the_spa_shell()
    {
        var (status, body, _) = await GetAsync("/api/orders");

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(body, Is.Not.EqualTo(_indexHtml));
        });
    }
}
