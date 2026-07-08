using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Hosting;

/// <summary>
/// Translates <see cref="WorkbenchException"/> into RFC 7807 ProblemDetails with the exception's status code and
/// error code. Registered in <see cref="WorkbenchHost"/>. (Moved out of Program.cs by #429 when the host bootstrap
/// was extracted into a reusable entry point.)
/// </summary>
internal sealed class WorkbenchExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not WorkbenchException wb) return false;

        // The client may have already disconnected (SSE close, page navigation, aborted fetch).
        // Once the response has started we can't rewrite status/headers, and writing into a dead
        // connection throws OperationCanceledException from the response pipe. Either way the
        // exception is still "handled" — returning true (rather than throwing) lets .NET 10's
        // exception-handler middleware suppress the error-level diagnostics it emits for unhandled
        // exceptions. Throwing here is what previously produced the "fail" log spam.
        if (httpContext.RequestAborted.IsCancellationRequested || httpContext.Response.HasStarted)
        {
            return true;
        }

        var problem = new ProblemDetails
        {
            Status = wb.StatusCode,
            Title = wb.ErrorCode,
            Detail = wb.Message,
            Type = $"https://typhon.dev/errors/{wb.ErrorCode}"
        };

        httpContext.Response.StatusCode = wb.StatusCode;
        httpContext.Response.ContentType = "application/problem+json";
        try
        {
            await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Client vanished mid-flush — nothing left to send; the exception is handled regardless.
        }
        return true;
    }
}
