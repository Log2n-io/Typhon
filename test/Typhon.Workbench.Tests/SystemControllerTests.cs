using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Typhon.Workbench.Controllers;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Covers <see cref="SystemController.GetOs"/> — the client uses the OS string to disable editor
/// choices that don't apply (e.g. Visual Studio on macOS). The endpoint is intentionally ungated
/// (no bootstrap-token attribute) since it leaks zero information beyond the system platform.
/// </summary>
[TestFixture]
public sealed class SystemControllerTests
{
    private WorkbenchFactory _factory;
    private HttpClient _client;

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task GetOs_ReturnsCurrentPlatform()
    {
        var resp = await _client.GetAsync("/api/system/os");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var info = await resp.Content.ReadFromJsonAsync<SystemController.OsInfo>();
        Assert.That(info, Is.Not.Null);

        var expected =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
            : "other";
        Assert.That(info.Os, Is.EqualTo(expected));
    }
}
