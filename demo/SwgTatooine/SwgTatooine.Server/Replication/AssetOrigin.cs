using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace SwgTatooine.Server;

/// <summary>
/// The published asset set — GLBs, descriptors, shared KTX2 textures — served as plain static HTTP and <b>pinned</b> for
/// the life of the process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Static files rather than object storage, and that is not a shortcut.</b> Every published name carries its revision
/// (<c>&lt;asset&gt;-&lt;rev&gt;.glb</c>) and the exporter refuses to give a published name new bytes, so a file never changes
/// and a change is a new name. That makes the hosting choice reversible: the client
/// learns where the assets are from <c>/typhon/demo.json</c> and from nowhere else, so moving this set behind S3 or a CDN
/// later is a configuration change with no code on either side. At a few megabytes in one folder, paying for credentials,
/// CORS and a second deployment today would buy nothing that cannot be bought on the day it matters.
/// </para>
/// <para>
/// <b>The server decides the version, and the client is told.</b> Server and client read the same descriptors — the server
/// for collision, portals and spawns, the client to draw them — so a run where the two disagree is a run where a wall is
/// in two places. <c>catalogue.json</c> is only a mutable pointer for tools; what is announced is the immutable
/// <c>catalogue-&lt;version&gt;.json</c> this process read at startup. A rollout therefore leaves old clients working,
/// because nothing they already fetched was overwritten.
/// </para>
/// <para>
/// <b>Not <c>MapStaticAssets</c>.</b> It fingerprints files again under names of its own, and our GLBs reference their
/// KTX2 textures <i>by published name</i> — those references would then resolve to the non-fingerprinted endpoint and be
/// served with revalidation instead of <c>immutable</c>. It also only knows the files present when the app was built,
/// while this set is deployed on its own schedule.
/// </para>
/// </remarks>
public sealed class AssetOrigin
{
    /// <summary>Where the set is served, without a trailing slash. The announced base is this plus one.</summary>
    public const string RequestPath = "/assets";

    /// <summary>The mutable pointer the exporter writes beside the immutable catalogues. Served, but never announced.</summary>
    public const string PointerName = "catalogue.json";

    /// <summary>
    /// The one catalogue schema this server and its client understand: 2, revision-named files and an integer version
    /// (<c>SwgTatooine.Assets/ASSET_FORMAT.md</c>).
    /// </summary>
    public const int Schema = 2;

    /// <summary>
    /// Extensions that have a brotli sidecar. KTX2 has none: it is already entropy-coded, and the measured gain is 0%.
    /// </summary>
    private static readonly string[] Precompressed = [".glb", ".json"];

    private AssetOrigin(string directory, string version, int assets, int textures)
    {
        Directory = directory;
        Version = version;
        AssetCount = assets;
        TextureCount = textures;
    }

    /// <summary>The published folder on disk. <b>Flat</b>, and it must stay flat — see <see cref="MapOn"/>.</summary>
    public string Directory { get; }

    /// <summary>The catalogue version this process is pinned to: a positive integer, as text.</summary>
    public string Version { get; }

    /// <summary>The immutable catalogue's file name, which is what the client is told to fetch.</summary>
    public string CatalogueName => $"catalogue-{Version}.json";

    /// <summary>How many assets that catalogue publishes, for the startup line.</summary>
    public int AssetCount { get; }

    /// <summary>How many shared textures it publishes, for the startup line.</summary>
    public int TextureCount { get; }

    /// <summary>
    /// Reads <paramref name="directory"/>'s pointer, pins the immutable catalogue it names, and refuses anything it cannot
    /// stand behind.
    /// </summary>
    /// <param name="directory">A published asset folder: <c>SwgTatooine.Assets/build/assets</c>, or a copy of it.</param>
    /// <returns>The pinned origin.</returns>
    /// <exception cref="ArgumentException">The folder, the pointer, or the catalogue it names, is missing or malformed.</exception>
    /// <remarks>
    /// <b>Fail at startup, loudly.</b> Every failure here is a deployment that unpacked wrong, and the alternative to
    /// refusing is a server that answers 404 for the world's geometry while looking healthy — which is then debugged in the
    /// browser, by somebody else, hours later.
    /// </remarks>
    public static AssetOrigin Open(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var root = Path.GetFullPath(directory.Trim());
        if (!System.IO.Directory.Exists(root))
        {
            throw new ArgumentException($"asset directory '{root}' does not exist.");
        }

        var pointer = Path.Combine(root, PointerName);
        if (!File.Exists(pointer))
        {
            throw new ArgumentException(
                $"'{root}' holds no {PointerName}, so it is not a published asset folder. Point --assets at SwgTatooine.Assets/build/assets "
                + "(python scripts/assets.py fetch published).");
        }

        var (version, assets, textures) = ReadCatalogue(File.ReadAllText(pointer), PointerName);
        var pinned = Path.Combine(root, $"catalogue-{version}.json");
        if (!File.Exists(pinned))
        {
            // The publish order is blobs, then the immutable catalogue, then the pointer (ASSET_PIPELINE.md 6.4). A pointer
            // whose immutable twin is absent means that order was not kept, and serving the pointer instead would pin this
            // run to a name that can change under it.
            throw new ArgumentException(
                $"{PointerName} names version '{version}' but '{Path.GetFileName(pinned)}' is not in '{root}'. The publish is incomplete.");
        }

        return new AssetOrigin(root, version, assets, textures);
    }

    /// <summary>
    /// The version and the counts of a catalogue body. Separate from the file so a test can reach it with a string.
    /// </summary>
    /// <param name="json">A catalogue document.</param>
    /// <param name="name">What to call it in an error message.</param>
    /// <returns>Its version, how many assets it lists and how many shared textures.</returns>
    /// <exception cref="ArgumentException">It is not a schema-2 catalogue, or its version is not a positive integer.</exception>
    internal static (string Version, int Assets, int Textures) ReadCatalogue(string json, string name)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new ArgumentException($"{name} is not JSON: {e.Message}", e);
        }

        using (doc)
        {
            var body = doc.RootElement;
            if (body.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException($"{name} is not a catalogue: its root is {body.ValueKind}, not an object.");
            }

            if (!body.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != Schema)
            {
                throw new ArgumentException(
                    $"{name} is not schema {Schema}. This server reads the same descriptors its client draws, so a schema it does not know is refused "
                    + "rather than half-understood.");
            }

            // It becomes a file name, a URL and a JSON string value, so it is read as what it is, an integer, and nothing a file
            // this process merely found on disk could smuggle into any of the three.
            if (!body.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var number) || number < 1)
            {
                throw new ArgumentException($"{name} has no positive integer 'version'.");
            }

            var text = number.ToString(CultureInfo.InvariantCulture);

            var assets = body.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Object ? CountProperties(a) : 0;
            var textures = body.TryGetProperty("textures", out var t) && t.ValueKind == JsonValueKind.Array ? t.GetArrayLength() : 0;
            if (assets == 0)
            {
                throw new ArgumentException($"{name} publishes no assets.");
            }

            return (text, assets, textures);
        }
    }

    /// <summary>What <c>/typhon/demo.json</c> adds for this origin: the base URL and the pinned catalogue's name.</summary>
    /// <returns>A JSON fragment beginning with a comma, to splice into that document.</returns>
    /// <param name="shapes">The collision geometry to publish with the table, or <see langword="null"/> for none.</param>
    internal string DemoJson(AssetColliders shapes = null)
    {
        // The asset a structure IS, as the index on the wire counts them. Published rather than mirrored: the server
        // picks which asset stands where, because it is the side that pushes bodies out of that asset's walls, and a
        // client holding its own copy of the rule would eventually disagree about which building it is looking at.
        var table = new System.Text.StringBuilder();
        foreach (var id in StructureAssets.Table)
        {
            if (table.Length > 0)
            {
                table.Append(',');
            }

            table.Append('"').Append(id).Append('"');
        }

        // The geometry the server collides against, so the client predicts the same push-out without re-deriving it
        // from the descriptors — and without needing to know which assets the blocking rule has been checked against.
        var collision = shapes == null ? "[]" : shapes.ToJson();
        var body = FormattableString.Invariant(
            $"\"base\":\"{RequestPath}/\",\"catalogue\":\"{CatalogueName}\",\"structures\":[{table}],\"collision\":{collision},\"creatures\":{CreatureJson()}");
        return ",\"assets\":{" + body + "}";
    }

    /// <summary>
    /// What each creature template IS, by the <c>template</c> a creature and a lair replicate: its name, the bodies its creatures wear (a creature's
    /// <c>skin</c> indexes them) and the prop at its lair's centre.
    /// </summary>
    /// <remarks>Published for the reason <see cref="StructureAssets.Table"/> is: the server picks what a hideout looks like, and a client keeping its own copy
    /// of that rule would eventually draw a different camp than the one the world holds.</remarks>
    internal static string CreatureJson()
    {
        var json = new System.Text.StringBuilder("[");
        for (var t = 0; t < CreatureTemplates.Count; t++)
        {
            if (t > 0)
            {
                json.Append(',');
            }

            json.Append("{\"name\":\"").Append(CreatureTemplates.Name(t)).Append("\",\"lair\":\"").Append(CreatureTemplates.LairAsset[t]).Append("\",\"bodies\":[");
            var bodies = CreatureTemplates.Bodies[t];
            for (var b = 0; b < bodies.Length; b++)
            {
                json.Append(b > 0 ? ",\"" : "\"").Append(bodies[b]).Append('"');
            }

            json.Append("]}");
        }

        return json.Append(']').ToString();
    }

    /// <summary>
    /// Serves this folder at <see cref="RequestPath"/>: revision names cached forever, brotli sidecars when the caller takes
    /// them, and the pointer revalidated.
    /// </summary>
    /// <param name="app">The host, before the client's own static files.</param>
    /// <remarks>
    /// <para>
    /// <b>One folder URL, flat.</b> Babylon's glTF loader keys its texture cache by <c>"data:" + rootUrl + uri</c>, so the
    /// shared KTX2 library collapses to one GPU texture per file only while every GLB is fetched from the same folder.
    /// Sharding this path into sub-folders, or serving some assets from somewhere else, multiplies VRAM silently.
    /// </para>
    /// <para>
    /// <b>No CORS headers.</b> The client is served by this same host, and in development Vite proxies <c>/assets</c> here,
    /// so every fetch is same-origin. A cross-origin deployment adds <c>Access-Control-Allow-Origin</c> at the CDN, which
    /// is where that decision belongs.
    /// </para>
    /// </remarks>
    internal void MapOn(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var files = new PhysicalFileProvider(Directory);
        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".glb"] = "model/gltf-binary";
        types.Mappings[".ktx2"] = "image/ktx2";

        // Brotli-11 is far too slow to run per request, so the exporter writes <file>.br beside each GLB and descriptor and
        // this hands them over as they are. Before the static files, because it works by rewriting the path they resolve.
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value;
            if (path != null
                && path.Length > RequestPath.Length + 1
                && path.StartsWith(RequestPath + "/", StringComparison.Ordinal)
                && HasSidecar(path)
                && AcceptsBrotli(context.Request.Headers.AcceptEncoding))
            {
                // Through the file provider rather than Path.Combine: it resolves a sub-path safely, so a request carrying
                // '..' probes nothing outside the folder.
                var sidecar = files.GetFileInfo(path[RequestPath.Length..] + ".br");
                if (sidecar.Exists)
                {
                    context.Request.Path = path + ".br";
                    context.Response.Headers.ContentEncoding = "br";
                    context.Response.Headers.Vary = "Accept-Encoding";
                }
            }

            await next();
        });

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            RequestPath = RequestPath,
            ContentTypeProvider = new SidecarContentTypes(types),
            OnPrepareResponse = context =>
            {
                var name = context.File.Name;
                var pointer = name.Equals(PointerName, StringComparison.Ordinal)
                    || name.Equals(PointerName + ".br", StringComparison.Ordinal);
                context.Context.Response.Headers.CacheControl = pointer ? "no-cache" : "public, max-age=31536000, immutable";
            },
        });
    }

    private static bool HasSidecar(string path)
    {
        foreach (var extension in Precompressed)
        {
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <c>br</c> is offered as a token with a non-zero quality, rather than merely present as a substring.
    /// </summary>
    /// <param name="header">The request's <c>Accept-Encoding</c>.</param>
    /// <returns><see langword="true"/> when the sidecar may be served.</returns>
    internal static bool AcceptsBrotli(string header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return false;
        }

        foreach (var range in header.Split(','))
        {
            var token = range.AsSpan().Trim();
            var semicolon = token.IndexOf(';');
            var coding = (semicolon < 0 ? token : token[..semicolon]).Trim();
            if (!coding.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (semicolon < 0)
            {
                return true;
            }

            // "br;q=0" and "br;q=0.0" are refusals; "br;q=0.5" is not. Nothing else about the quality matters here, because
            // the only alternative on offer is the identity encoding of the same bytes.
            var quality = token[(semicolon + 1)..].Trim();
            return !quality.StartsWith("q=0", StringComparison.OrdinalIgnoreCase) || quality.IndexOfAnyExcept("q=0.".AsSpan()) >= 0;
        }

        return false;
    }

    private static int CountProperties(JsonElement element)
    {
        var count = 0;
        foreach (var unused in element.EnumerateObject())
        {
            _ = unused;
            count++;
        }

        return count;
    }

    /// <summary>
    /// The content type of <c>x.glb.br</c> is the content type of <c>x.glb</c>: the sidecar is the same resource in a
    /// transfer encoding, and <c>Content-Encoding</c> already says so.
    /// </summary>
    private sealed class SidecarContentTypes : IContentTypeProvider
    {
        private readonly IContentTypeProvider _inner;

        public SidecarContentTypes(IContentTypeProvider inner) => _inner = inner;

        public bool TryGetContentType(string subpath, out string contentType) =>
            _inner.TryGetContentType(subpath.EndsWith(".br", StringComparison.OrdinalIgnoreCase) ? subpath[..^3] : subpath, out contentType);
    }
}
