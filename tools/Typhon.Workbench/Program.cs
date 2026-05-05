using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Typhon.Workbench.Hosting;
using Typhon.Workbench.Security;
using Typhon.Workbench.Sessions;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers(o =>
    {
        // Force every action to advertise (and return) application/json only. By default MVC also
        // lists text/json and text/plain in the OpenAPI "produces" for JSON responses, which makes
        // Orval 8 emit a discriminated union of three media types per response — garbage at the
        // call site. The Workbench never speaks text/plain for DTOs, so we strip those formatters
        // from the content-negotiation pipeline entirely.
        o.Filters.Add(new ProducesAttribute("application/json"));
    })
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        // String-encode enums camelCase (matches the TS client which uses 'vsCode' | 'rider' …).
        // Without this, PATCH /api/options/editor 400s because the server can't deserialize "rider"
        // to the EditorKind enum.
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    });

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<WorkbenchExceptionHandler>();
builder.Services.AddWorkbenchServices();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// OpenAPI document at /openapi.json — stable path agreed upon by Orval and the Vite proxy.
app.MapOpenApi("/openapi.json");
app.MapControllers();
app.MapWorkbenchEndpoints();

app.Services.RegisterSessionShutdownHook();

// Eagerly materialize the bootstrap token so the file is written before any client tries to read
// it (Vite dev proxy, Playwright runs, launcher child processes). The constructor performs the
// disk write.
var gate = app.Services.GetRequiredService<BootstrapTokenGate>();
app.Logger.LogInformation("Workbench bootstrap token written to {Path}", gate.TokenFilePath);

// Sweep orphan profiler temp files left by prior crashes. Live attach sessions write LZ4-compressed chunks
// to %TEMP%/typhon-workbench/{sessionId}.cache; on graceful shutdown the file is deleted, but a crash leaves it.
LiveCacheTempFile.SweepOrphans(app.Logger);

app.Run();

/// <summary>
/// Translates WorkbenchException into RFC 7807 ProblemDetails with the exception's status code and error code.
/// </summary>
internal sealed class WorkbenchExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not WorkbenchException wb) return false;

        var problem = new ProblemDetails
        {
            Status = wb.StatusCode,
            Title = wb.ErrorCode,
            Detail = wb.Message,
            Type = $"https://typhon.dev/errors/{wb.ErrorCode}"
        };

        httpContext.Response.StatusCode = wb.StatusCode;
        httpContext.Response.ContentType = "application/problem+json";
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}

// Exposes the implicit Program class for WebApplicationFactory<Program> in tests.
public partial class Program { }
