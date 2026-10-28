# Typhon.Profiler.AspNetCore

**Ties the HTTP requests an ASP.NET Core host serves to the [Typhon](https://typhondb.io) engine work they cause.**

With the profiler's correlation switch on (`Typhon:Profiler:Correlation:Enabled`):
- Every request becomes a root in the Typhon Workbench.
- Every engine span the request causes carries the request's W3C trace id, on whatever thread its `await`s land.
- An incoming `traceparent` is honoured.

> ⚠️ **Alpha.** APIs and the wire format may still change between releases.

```csharp
builder.Services.AddTyphonRequestCorrelation();
```

That is the whole setup. With correlation off it registers nothing, and requests pay nothing.

Work a request hands to another thread (a write staged into the tick, say) has to carry its cause explicitly:
- `CausalScope.Capture()` where the work is queued;
- `using var _ = CausalScope.Enter(captured);` where it runs.
