using System.Net;
using BFF.Proxy.Tests.Integration.Infrastructure;

namespace BFF.Proxy.Tests.Integration.Samples;

// samples/multi-spa: COPY app/ ./wwwroot/app/ and COPY admin/ ./wwwroot/admin/ - each wwwroot subdirectory with an
// index.html is auto-mounted at /<name>/... (MapStaticSpaMounts).
public class MultiSpaSampleTests : BffIntegrationTest
{
    private string _appHtml = null!;
    private string _adminHtml = null!;

    protected override string CreateContentRoot()
    {
        _appHtml = SampleLayout.ReadSampleFile("multi-spa/app/index.html");
        _adminHtml = SampleLayout.ReadSampleFile("multi-spa/admin/index.html");
        var layout = DisposeAfterFixture(SampleLayout.Create())
            .CopyDirectory("multi-spa/app", "wwwroot/app")
            .CopyDirectory("multi-spa/admin", "wwwroot/admin")
            // Not part of the sample: the guards around it. A folder named like a BFF prefix must never be
            // mounted, and a folder without index.html is just static files.
            .WriteFile("wwwroot/bff/index.html", "<p>must not be served</p>")
            .WriteFile("wwwroot/api/index.html", "<p>must not be served</p>")
            .WriteFile("wwwroot/assets/logo.txt", "logo");
        return layout.ContentRoot;
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string path, BffClient? client = null)
    {
        using var response = await (client ?? AnonymousClient()).Http.GetAsync(path);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [TestCase("/app")]
    [TestCase("/app/")]
    [TestCase("/app/orders/42")]
    [TestCase("/app/index.html")]
    public async Task App_and_its_client_routes_serve_the_app_shell(string path)
    {
        var (status, body) = await GetAsync(path);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body, Is.EqualTo(_appHtml));
        });
    }

    [TestCase("/admin")]
    [TestCase("/admin/users/7/edit")]
    public async Task Admin_and_its_client_routes_serve_the_admin_shell(string path)
    {
        var (status, body) = await GetAsync(path);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body, Is.EqualTo(_adminHtml));
        });
    }

    [TestCase("/")]
    [TestCase("/dashboard")]
    public async Task Paths_outside_the_mounts_are_404_without_a_top_level_index(string path)
    {
        var (status, _) = await GetAsync(path);

        Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Missing_asset_inside_a_mount_is_a_404_not_the_shell()
    {
        var (status, _) = await GetAsync("/app/assets/missing.js");

        Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Folder_without_index_html_serves_its_files_but_is_not_a_mount()
    {
        var (fileStatus, fileBody) = await GetAsync("/assets/logo.txt");
        var (routeStatus, _) = await GetAsync("/assets/some/route");

        Assert.Multiple(() =>
        {
            Assert.That(fileStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(fileBody, Is.EqualTo("logo"));
            Assert.That(routeStatus, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [TestCase("/bff/index.html")]
    [TestCase("/bff/some/route")]
    public async Task Wwwroot_folder_named_bff_is_never_served(string path)
    {
        var (status, body) = await GetAsync(path);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(body, Does.Not.Contain("must not be served"));
        });
    }

    [Test]
    public async Task Wwwroot_folder_named_api_does_not_shadow_the_backend()
    {
        var client = await SignedInClientAsync();

        var (status, body) = await GetAsync("/api/index.html", client);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body, Is.EqualTo("backend-ok"));
        });
    }
}
