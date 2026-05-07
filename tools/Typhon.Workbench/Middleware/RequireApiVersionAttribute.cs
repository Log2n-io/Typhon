using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Typhon.Workbench.Middleware;

/// <summary>
/// Validates the optional <c>X-Workbench-Api</c> request header and enforces API version compatibility.
/// Missing header soft-launches as v1. A present, parseable, matching version continues. Any other
/// case short-circuits with a structured ProblemDetails response.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireApiVersionAttribute : Attribute, IAsyncActionFilter
{
    private const string HeaderName = "X-Workbench-Api";
    private const int SupportedVersion = 1;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var raw))
        {
            // Soft-launch: missing header defaults to v1.
            context.HttpContext.Items["ApiVersion"] = SupportedVersion;
            await next();
            return;
        }

        if (!int.TryParse(raw, out var version))
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Type = "invalid-api-version",
                Title = "Invalid API version",
                Detail = $"The {HeaderName} header value '{raw}' is not a valid integer.",
                Status = StatusCodes.Status400BadRequest,
            })
            { StatusCode = StatusCodes.Status400BadRequest };
            return;
        }

        if (version != SupportedVersion)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Type = "api-version-mismatch",
                Title = "API version mismatch",
                Detail = $"This endpoint requires API version {SupportedVersion}; client sent version {version}.",
                Status = StatusCodes.Status426UpgradeRequired,
            })
            { StatusCode = StatusCodes.Status426UpgradeRequired };
            return;
        }

        context.HttpContext.Items["ApiVersion"] = SupportedVersion;
        await next();
    }
}
