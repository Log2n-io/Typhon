using Microsoft.AspNetCore.Mvc;
using Typhon.Engine;
using Typhon.Workbench.Dtos.Realms;
using Typhon.Workbench.Middleware;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// The realm catalog: what worlds this database holds, and whatever any one of them can be known to be doing from here (#1083).
/// </summary>
/// <remarks>
/// <para>
/// <b>Gated on <see cref="SessionCapability.Realms"/> rather than on the session kind</b>, because the two session kinds hold opposite halves of the answer and
/// neither is "the" realm session. An Open session has the catalog and nothing running; an Attach session has run state over a database it cannot browse. A route
/// that asked "is this an OpenSession?" would be right today and wrong the moment the attach half lands.
/// </para>
/// <para>
/// <b>Read-only, as every other pillar is.</b> Registering, unregistering or re-policying a realm is the application's, and a realm whose id was reused after a
/// Workbench click would be a database corruption with a UI in front of it.
/// </para>
/// </remarks>
[ApiController]
[Route("api/sessions/{sessionId:guid}/realms")]
[Tags("Realms")]
[RequireBootstrapToken]
[RequireSession]
public sealed class RealmsController : WorkbenchControllerBase
{
    /// <summary>
    /// Why an Open session cannot answer the live half. Shown verbatim, so it explains rather than blames.
    /// </summary>
    /// <remarks>
    /// <b>"Not in the catalog" is the literal reason, and it is stronger than "nothing is ticking".</b> The engine invents a policy for a realm the application
    /// did not register — <c>RealmConfig.SimulatedAlways(persisted)</c> in <c>DatabaseEngine</c>'s open-time merge — so a realm table recovered from the catalog
    /// alone carries a divisor and a run state that no application ever chose. They are readable, and reporting them would be reporting a default as a decision.
    /// </remarks>
    private const string NotRunningReason =
        "This database was opened as a file, so its realms were recovered from the catalog rather than registered by the application. Policy, run state and "
        + "session counts are the application's to supply at each open and are not persisted, so nothing here can know them. Attach to a running engine to see "
        + "them.";

    /// <summary>Why a realm has no catalog row.</summary>
    private const string LifecycleUnknown = "unknown";

    [HttpGet]
    public ActionResult<RealmListDto> GetRealms(Guid sessionId)
    {
        if (HttpContext.Items["Session"] is not Sessions.ISession session || !session.Capabilities.Contains(SessionCapability.Realms))
        {
            return ConflictKindMismatch(
                "Realms are available for a session that has a realm-capable database or a live engine. A single-realm database has no realms to list.");
        }

        if (session is not OpenSession open || open.Engine?.Engine == null)
        {
            // An attach session HAS the capability and legitimately reaches this route — and still gets nothing here, because this route reads the realm
            // CATALOG out of the file and a running engine holds that file exclusively (blocker B1). Its realm rows travel a different road entirely: one
            // per runnable realm per tick, on the spatial trace stream, which the live realm board reads client-side from the records it is already
            // receiving. An empty list would read as "this engine has no realms", which is the one thing it is not.
            return ConflictKindMismatch(
                "A live attach session's realms arrive on the trace stream, not through this route — the realm catalog is read from the database file, and "
                + "the running engine holds it. Use the live realm board, or open the file to browse the catalog.");
        }

        return Ok(BuildList(open.Engine.Engine));
    }

    /// <summary>
    /// Every realm the engine can name, ascending by id.
    /// </summary>
    /// <remarks>
    /// <b>Three sources, unioned, and the row says which it came from.</b> The persisted catalog is the durable truth for every realm except 0; the live realm
    /// table is the only place a registered realm's policy and state exist; and realm 0 is in neither, because it keeps the single-world grid record so that a
    /// database which never named a realm stays byte-for-byte what it was. A list built from any one of the three would be missing realms that exist.
    /// </remarks>
    private static RealmListDto BuildList(DatabaseEngine engine)
    {
        var table = engine.RealmTable;
        var catalog = engine.PersistedRealmCatalog;
        var rows = new SortedDictionary<int, RealmDto>();
        var catalogued = 0;

        if (catalog != null)
        {
            foreach (var (id, entry) in catalog)
            {
                catalogued++;
                rows[id] = FromCatalog(entry.Row);
            }
        }

        // The live table second, so a registered realm reports its live grid and the fact of registration. Not its policy: see FromRegistered.
        if (table != null)
        {
            foreach (var realm in table.Registered)
            {
                rows[realm.Id.Value] = FromRegistered(engine, realm);
            }
        }

        // Realm 0 last, and only if nothing above named it: on a running engine it is registered and already has its live row.
        //
        // <b>Read from the bootstrap record rather than from Realm0Grid</b>, which resolves through the realm table — absent on a schema-less open, which is
        // exactly the session this route most has to serve. Realm 0 is in neither the catalog nor that table then, and omitting it would drop the realm every
        // database has from the list of realms.
        if (!rows.ContainsKey(RealmId.Default.Value) && engine.TryReadPersistedRealm0Grid(out var realm0Grid))
        {
            rows[RealmId.Default.Value] = new RealmDto(
                RealmId.Default.Value,
                engine.Realms.GenerationOf(RealmId.Default),
                "grid",
                Registered: false,
                LifecycleUnknown,
                GridOf(in realm0Grid),
                RunState: string.Empty,
                Divisor: null,
                Sessions: null,
                Kind: string.Empty);
        }

        var list = new RealmDto[rows.Count];
        rows.Values.CopyTo(list, 0);

        // The id space a caller should consider, which is NOT simply Realms.MaxRealms: the capacity is raised from the catalog during InitializeArchetypes, and
        // a Workbench open with no schema assemblies never gets there — such a session reads MaxRealms as 1 over a database holding realm 3. Taking the wider of
        // the two means the figure describes the database rather than how this process happened to open it.
        var widest = engine.Realms.MaxRealms;
        foreach (var row in list)
        {
            widest = Math.Max(widest, row.Id + 1);
        }

        return new RealmListDto(widest, list, LiveState: false, NotRunningReason, catalogued);
    }

    private static RealmDto FromCatalog(in RealmR1 row) => new(
        row.Id,
        row.Generation,
        "catalog",
        Registered: false,
        LifecycleOf(row.State),
        new RealmGridDto(
            row.WorldMinX,
            row.WorldMinY,
            row.WorldMinZ,
            row.WorldMaxX,
            row.WorldMaxY,
            row.WorldMaxZ,
            row.CellSize,
            row.MigrationHysteresisRatio,
            IsDeep(row.WorldMaxZ - row.WorldMinZ, row.CellSize)),
        RunState: string.Empty,
        Divisor: null,
        Sessions: null,
        Kind: string.Empty);

    /// <summary>
    /// A realm the engine holds in its realm table right now: the authoritative grid, its generation, and the fact of registration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The policy fields stay null even though <c>realm.Config</c> is right there, and that is the whole point.</b> A Workbench never registers a realm, so
    /// every realm in a session like this one was recovered from the catalog — and the engine's open-time merge gives a recovered realm
    /// <c>RealmConfig.SimulatedAlways(persisted)</c>, a default invented so that there is something rather than a decision anyone made. Its
    /// <c>UnobservedTickDivisor</c>, its replication kind and the run state derived from them are readable and meaningless. Rendering "Simulated, divisor 1" for
    /// a realm the application sleeps would be worse than rendering nothing, because it looks like an answer.
    /// </para>
    /// <para>
    /// The grid is the opposite case and is reported from here in preference to the catalog row: a registration whose grid contradicts the catalog is refused at
    /// open (RLM-01), so the live grid and the persisted one agree by construction, and the live one carries the tuning fields the row does not.
    /// </para>
    /// </remarks>
    private static RealmDto FromRegistered(DatabaseEngine engine, Realm realm) => new(
        realm.Id.Value,
        engine.Realms.GenerationOf(realm.Id),
        "registered",
        Registered: true,
        realm.Closing ? "closing" : "live",
        GridOf(realm.Grid.Config),
        RunState: string.Empty,
        Divisor: null,
        Sessions: null,
        Kind: string.Empty);

    private static RealmGridDto GridOf(in SpatialGridConfig config) => new(
        config.WorldMin.X,
        config.WorldMin.Y,
        config.WorldMin.Z,
        config.WorldMax.X,
        config.WorldMax.Y,
        config.WorldMax.Z,
        config.CellSize,
        config.MigrationHysteresisRatio,
        Deep: config.GridDepth > 1);

    /// <summary>
    /// Whether a Z extent makes a realm three-dimensional: more than ONE cell deep, not merely a non-zero extent.
    /// </summary>
    /// <remarks>
    /// <b>A flat realm's Z extent is one cell, not zero</b> — <c>SpatialGridConfig.Flat</c> builds <c>WorldMax.Z = cellSize</c>, so "deep when MaxZ > MinZ" calls
    /// every flat realm deep. The live config answers this exactly with <c>GridDepth</c> (cells along Z); a catalog row carries only bounds and a cell size, so
    /// it is recomputed the same way the config's own constructor does, with a half-cell tolerance for the f32 rounding an older record may carry.
    /// </remarks>
    private static bool IsDeep(double zExtent, double cellSize) => cellSize > 0 && zExtent > cellSize * 1.5;

    private static string LifecycleOf(int state) => state switch
    {
        RealmR1.StateLive => "live",
        RealmR1.StateClosing => "closing",
        RealmR1.StateRetired => "retired",
        _ => LifecycleUnknown,
    };
}
