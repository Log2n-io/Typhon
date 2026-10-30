using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Storage;

namespace Typhon.Workbench.Tests;

/// <summary>The repair endpoint's refusal of a database another process holds (IR-02).</summary>
[TestFixture]
public sealed class IntegrityControllerTests
{
    private WorkbenchFactory _factory;
    private HttpClient _client;
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-wb-integrity-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    [Test]
    [CancelAfter(15_000)]
    public async Task Apply_OnABundleAnotherHandleHolds_IsAConflict()
    {
        var bundle = Path.Combine(_tempDir, "held.typhon");
        Directory.CreateDirectory(bundle);
        var data = Path.Combine(bundle, "data");
        await File.WriteAllBytesAsync(data, new byte[8192]);

        // The engine's own hold: the refusal comes before the scan, so the bytes need not be a database.
        using var engineHandle = File.OpenHandle(data, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

        var response = await _client.PostAsJsonAsync("/api/integrity/apply", new RepairApplyRequestDto(bundle, "a-reviewed-plan"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.That(body.RootElement.GetProperty("error").GetString(), Does.Contain("open in another process"));
    }
}
