using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Typhon.Workbench.Middleware;

namespace Typhon.Workbench.Security;

/// <summary>
/// Document transformer that registers the Workbench's three credential channels in the OpenAPI
/// document so debug UIs (Scalar / Swagger UI / etc.) can render the right "Authorize" dialog and
/// know which header to attach.
///
/// <list type="bullet">
///   <item><c>Bearer</c> — <c>Authorization: Bearer &lt;PAT&gt;</c>. Long-lived, user-minted via the
///       <c>--new-token</c> CLI flag. Equivalent to the bootstrap token for auth purposes.</item>
///   <item><c>WorkbenchToken</c> — <c>X-Workbench-Token</c> apiKey. The per-process bootstrap token
///       used by the bundled SPA. Documented for completeness; PATs are the better choice for
///       human / scripting use.</item>
///   <item><c>SessionToken</c> — <c>X-Session-Token</c> apiKey. Required additionally on
///       session-scoped routes (<c>RequireSessionAttribute</c>); the value is the session id GUID
///       returned by <c>POST /api/sessions/*</c>.</item>
/// </list>
///
/// Security schemes are metadata — enforcement remains in <c>RequireBootstrapTokenAttribute</c> /
/// <c>RequireSessionAttribute</c>. The Bearer scheme is also tagged as a per-operation
/// <c>SecurityRequirement</c> so Scalar's API explorer surfaces an "Authenticate" affordance and
/// attaches the <c>Authorization</c> header automatically once the user pastes a PAT.
/// </summary>
internal sealed class WorkbenchSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        var schemes = document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        schemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "PAT (64-char hex)",
            Description = "Personal Access Token minted via the `--new-token <name>` CLI flag. Stored hash at "
                + "`%LOCALAPPDATA%/Typhon/Workbench/tokens/<name>.token`. Sent as `Authorization: Bearer <token>`.",
        };

        schemes["WorkbenchToken"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "X-Workbench-Token",
            Description = "Per-process bootstrap token at `%LOCALAPPDATA%/Typhon/Workbench/bootstrap.token`. "
                + "Equivalent to the Bearer PAT for auth purposes — pick one. Regenerated each time the Workbench restarts.",
        };

        schemes["SessionToken"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "X-Session-Token",
            Description = "Session id GUID returned by `POST /api/sessions/file|attach|trace`. "
                + "Required IN ADDITION to the Bearer / X-Workbench-Token credential on session-scoped routes "
                + "(those whose path contains `{sessionId}`). Must match the URL's session id.",
        };

        return Task.CompletedTask;
    }
}

/// <summary>
/// Operation-level transformer that stamps every endpoint with its required security schemes.
/// Bearer is required everywhere (since the auth filter runs on every action). Session-scoped
/// routes — those carrying <see cref="RequireSessionAttribute"/> — additionally require the
/// X-Session-Token header. By declaring both in a single <see cref="OpenApiSecurityRequirement"/>
/// (dictionary semantics = AND), Scalar's Authorize dialog knows to attach BOTH headers when the
/// user fills both in.
/// </summary>
internal sealed class WorkbenchSecurityRequirementTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var requiresSession = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<RequireSessionAttribute>()
            .Any();

        operation.Security ??= [];
        var requirement = new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = [],
        };
        if (requiresSession)
        {
            requirement[new OpenApiSecuritySchemeReference("SessionToken", context.Document)] = [];
        }
        operation.Security.Add(requirement);
        return Task.CompletedTask;
    }
}
