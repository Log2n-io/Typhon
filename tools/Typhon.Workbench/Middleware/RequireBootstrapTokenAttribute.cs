using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Net.Http.Headers;
using Typhon.Workbench.Security;

namespace Typhon.Workbench.Middleware;

/// <summary>
/// Authenticates an MVC API request against either of two credential sources:
///   1. <see cref="BootstrapTokenGate.HeaderName"/> header — the per-process bootstrap token used
///      by the bundled SPA / Vite proxy.
///   2. <c>Authorization: Bearer &lt;pat&gt;</c> — a Personal Access Token minted by the user via
///      the <c>--new-token</c> CLI flag, used by external tooling that lives across process
///      restarts.
///
/// Either credential is sufficient. If neither is present or valid, the request is rejected with
/// 401. See <see cref="BootstrapTokenGate"/> and <see cref="PersonalAccessTokenStore"/> for the
/// threat model — both are local-machine credentials that the browser sandbox cannot read.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireBootstrapTokenAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        var headers = context.HttpContext.Request.Headers;

        var gate = services.GetRequiredService<BootstrapTokenGate>();
        if (headers.TryGetValue(BootstrapTokenGate.HeaderName, out var bootstrapHeader)
            && gate.Validate(bootstrapHeader))
        {
            await next();
            return;
        }

        // Fallback: Authorization: Bearer <PAT>. Parse defensively — split on the first space and
        // accept the Bearer scheme case-insensitively per RFC 7235 §2.1. Anything malformed falls
        // through to the 401 below.
        var pats = services.GetService<PersonalAccessTokenStore>();
        if (pats is not null
            && headers.TryGetValue(HeaderNames.Authorization, out var authHeader)
            && authHeader.Count == 1
            && TryExtractBearer(authHeader.ToString(), out var presentedToken)
            && pats.Validate(presentedToken))
        {
            await next();
            return;
        }

        context.Result = new ObjectResult(new ProblemDetails
        {
            Title = "bootstrap_token_required",
            Detail = $"Missing or invalid {BootstrapTokenGate.HeaderName} header (or Authorization: Bearer PAT).",
            Status = StatusCodes.Status401Unauthorized,
        })
        { StatusCode = StatusCodes.Status401Unauthorized };
    }

    private static bool TryExtractBearer(string headerValue, out string token)
    {
        token = null;
        if (string.IsNullOrWhiteSpace(headerValue)) return false;

        var spaceIdx = headerValue.IndexOf(' ');
        if (spaceIdx <= 0) return false;

        var scheme = headerValue.AsSpan(0, spaceIdx);
        if (!scheme.Equals(PersonalAccessTokenStore.AuthorizationScheme.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = headerValue.AsSpan(spaceIdx + 1).Trim();
        if (rest.IsEmpty) return false;

        token = rest.ToString();
        return true;
    }
}
