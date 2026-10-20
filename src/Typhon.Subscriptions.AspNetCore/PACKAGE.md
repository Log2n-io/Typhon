# Typhon.Subscriptions.AspNetCore

**Hosts [Typhon](https://typhondb.io) Subscriptions in an ASP.NET Core application.**

The engine ships its own TCP listener. This package adds the WebSocket endpoint browsers need, and the catalog endpoint, to a host you
already run — so replication shares your server's port, TLS and middleware.

> ⚠️ **Alpha.** APIs and the wire format may still change between releases.

```csharp
builder.Services.AddSingleton(runtime);                                        // the TyphonRuntime whose Subscriptions you declared
builder.Services.AddTyphonSubscriptions(o => o.AllowOrigin("https://play.example.com"));

var app = builder.Build();
app.UseWebSockets();
app.MapTyphonSubscriptions("/ws");                                              // the typhon.3 WebSocket endpoint
app.MapTyphonCatalog("/typhon/catalog.json");                                   // the catalog, for tools and code generation
app.Run();
```

## Learn more

- [Transports & hosting](https://doc.typhondb.io/latest/guides/feature-catalog/Subscriptions/transports-hosting.html)
- [Subscriptions guide](https://doc.typhondb.io/latest/guides/07-subscriptions.html)
- Packages: [`Typhon`](https://www.nuget.org/packages/Typhon) (the engine) · [`Typhon.Client`](https://www.nuget.org/packages/Typhon.Client) (the .NET client)
