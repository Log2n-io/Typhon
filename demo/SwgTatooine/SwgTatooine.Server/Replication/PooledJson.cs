using System;
using System.Buffers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace SwgTatooine.Server;

/// <summary>
/// JSON answers built without a string (Loïc, 2026-11-05: the server allocated 20 MB/s, the polled endpoints a third of it): built into the request thread's
/// own builder, kept from request to request, encoded into a pooled buffer, written, and the buffer given back. What is left a request is the result itself.
/// </summary>
internal static class PooledJson
{
    [ThreadStatic] private static StringBuilder t_json;
    [ThreadStatic] private static Encoder t_utf8;

    /// <summary>The answer <paramref name="build"/> writes, as <c>application/json</c>; <paramref name="state"/> carries what it reads, so it captures nothing.</summary>
    public static IResult Of<TState>(TState state, Action<StringBuilder, TState> build)
    {
        var json = t_json ??= new StringBuilder(4096);
        json.Clear();
        build(json, state);
        var utf8 = t_utf8 ??= new UTF8Encoding(false).GetEncoder();
        utf8.Reset();
        var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(json.Length));
        var length = 0;
        foreach (var chunk in json.GetChunks())
        {
            length += utf8.GetBytes(chunk.Span, buffer.AsSpan(length), flush: false);
        }

        length += utf8.GetBytes([], buffer.AsSpan(length), flush: true);

        // A builder grown past a big answer once is not kept at that size by every thread for ever.
        if (json.Capacity > 1 << 20)
        {
            t_json = null;
        }

        return new Pooled(buffer, length);
    }

    /// <summary>Bytes encoded once and answered as they are: an answer identical for every caller of one snapshot.</summary>
    public static IResult Of(byte[] utf8) => new Fixed(utf8);

    private sealed class Pooled(byte[] buffer, int length) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            try
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength = length;
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, length), context.RequestAborted).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private sealed class Fixed(byte[] utf8) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength = utf8.Length;
            return context.Response.Body.WriteAsync(utf8, context.RequestAborted).AsTask();
        }
    }
}
