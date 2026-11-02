using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Typhon.Engine.Tests;

/// <summary>
/// An <see cref="ILoggerProvider"/> that keeps every Warning-level message, for tests that assert a warning fired (or did not). Register it beside the
/// fixture's own provider and rebuild the service provider, so the engine's <c>ILogger&lt;DatabaseEngine&gt;</c> reaches it.
/// </summary>
internal sealed class WarningCapture : ILoggerProvider
{
    private readonly List<string> _warnings = [];

    /// <summary>How many captured warnings contain <paramref name="fragment"/>.</summary>
    public int Matching(string fragment)
    {
        lock (_warnings)
        {
            var n = 0;
            foreach (var w in _warnings)
            {
                if (w.Contains(fragment, StringComparison.Ordinal))
                {
                    n++;
                }
            }
            return n;
        }
    }

    public ILogger CreateLogger(string categoryName) => new Capturing(this);

    public void Dispose() { }

    private sealed class Capturing(WarningCapture owner) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (logLevel != LogLevel.Warning)
            {
                return;
            }
            lock (owner._warnings)
            {
                owner._warnings.Add(formatter(state, exception));
            }
        }
    }
}
