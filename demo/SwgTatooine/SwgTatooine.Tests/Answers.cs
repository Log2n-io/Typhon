using System.IO;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace SwgTatooine.Tests;

/// <summary>Test helpers for an endpoint's answer.</summary>
internal static class Answers
{
    /// <summary>What a result writes to the response — a pooled JSON answer (PooledJson) writes its bytes, not a string: run it into memory and read it.</summary>
    public static string Written(IResult result, HttpContext context)
    {
        var body = new MemoryStream();
        context.Response.Body = body;
        result.ExecuteAsync(context).GetAwaiter().GetResult();
        return Encoding.UTF8.GetString(body.ToArray());
    }
}
