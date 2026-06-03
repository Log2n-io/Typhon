using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Typhon.Workbench.Hosting;

/// <summary>
/// File-backed persistence + hot-reload for <see cref="WorkbenchOptions"/>. Atomic write via
/// temp file + rename so a crash mid-save can't corrupt the JSON. <see cref="FileSystemWatcher"/>
/// handles out-of-band edits (e.g., user editing the JSON by hand): debounced 200 ms to coalesce
/// the multi-event burst most editors emit on save.
///
/// Storage path: <c>%LOCALAPPDATA%\Typhon.Workbench\options.json</c> on Windows,
/// <c>~/Library/Application Support/Typhon.Workbench/options.json</c> on macOS.
/// </summary>
/// <remarks>
/// <b>Concurrency:</b> two locks. <c>_lock</c> guards <c>_current</c> and is held only for the
/// brief in-memory swap; reads serialize behind it but never block on disk. <c>_writeLock</c>
/// serializes the atomic file-replacement so two concurrent <c>PATCH</c> requests can't collide on
/// the temp-file rename. Writes happen <i>outside</i> <c>_lock</c>, so a slow disk doesn't stall
/// <see cref="Get"/>.
/// </remarks>
public sealed partial class OptionsStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    private readonly string _filePath;
    private readonly ILogger<OptionsStore> _logger;
    private readonly FileSystemWatcher _watcher;
    private readonly object _lock = new();
    private readonly object _writeLock = new();
    private readonly System.Threading.Timer _debounceTimer;
    private WorkbenchOptions _current;

    /// <summary>Fired when the on-disk file changes (after debounce + reload).</summary>
    public event Action<WorkbenchOptions> OptionsChanged;

    public OptionsStore(ILogger<OptionsStore> logger) : this(logger, DefaultDirectory()) { }

    /// <summary>Test-friendly constructor: store options in <paramref name="directory"/> instead of LocalApplicationData.</summary>
    public OptionsStore(ILogger<OptionsStore> logger, string directory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var dir = directory ?? throw new ArgumentNullException(nameof(directory));
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "options.json");

        _current = TryLoad() ?? new WorkbenchOptions();

        var watcherDir = dir;
        _watcher = new FileSystemWatcher(watcherDir, "options.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += (s, e) => OnFileChanged(s, e);

        _debounceTimer = new System.Threading.Timer(_ => DebouncedReload(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string FilePath => _filePath;

    public WorkbenchOptions Get()
    {
        lock (_lock) { return _current; }
    }

    /// <summary>Replace the whole options document. Atomic on disk; broadcasts to subscribers.</summary>
    public void Replace(WorkbenchOptions next)
    {
        if (next == null) throw new ArgumentNullException(nameof(next));
        lock (_lock) { _current = next; }
        OptionsChanged?.Invoke(next);
        WriteAtomic(next);
    }

    /// <summary>Patch the Editor sub-record. Other categories untouched.</summary>
    public void PatchEditor(EditorOptions editor)
    {
        if (editor == null) throw new ArgumentNullException(nameof(editor));
        WorkbenchOptions next;
        lock (_lock)
        {
            next = _current with { Editor = editor };
            _current = next;
        }
        OptionsChanged?.Invoke(next);
        WriteAtomic(next);
    }

    /// <summary>Patch the Profiler sub-record. Other categories untouched.</summary>
    public void PatchProfiler(ProfilerOptions profiler)
    {
        if (profiler == null) throw new ArgumentNullException(nameof(profiler));
        WorkbenchOptions next;
        lock (_lock)
        {
            next = _current with { Profiler = profiler };
            _current = next;
        }
        OptionsChanged?.Invoke(next);
        WriteAtomic(next);
    }

    /// <summary>Patch the Schema sub-record (registered schema-assembly directories). Other categories untouched.</summary>
    public void PatchSchema(SchemaOptions schema)
    {
        if (schema == null) throw new ArgumentNullException(nameof(schema));
        WorkbenchOptions next;
        lock (_lock)
        {
            next = _current with { Schema = schema };
            _current = next;
        }
        OptionsChanged?.Invoke(next);
        WriteAtomic(next);
    }

    private WorkbenchOptions TryLoad()
    {
        try
        {
            if (!File.Exists(_filePath)) return null;
            using var stream = File.OpenRead(_filePath);
            return JsonSerializer.Deserialize<WorkbenchOptions>(stream, JsonOpts);
        }
        catch (Exception ex)
        {
            // Treat parse / IO errors as "no file" — keep defaults rather than crashing the host.
            LogLoadFailed(ex, _filePath);
            return null;
        }
    }

    /// <summary>
    /// Atomic write: serialize to a temp file, then rename over the target. A crash mid-write leaves
    /// either the old file (if temp not yet renamed) or the new file (rename is atomic on most FSes).
    /// Serialized via <see cref="_writeLock"/> so concurrent <c>PATCH</c>es don't collide on the
    /// temp-file path; readers (<see cref="Get"/>) hold the in-memory <see cref="_lock"/> only and
    /// never block on disk.
    /// </summary>
    private void WriteAtomic(WorkbenchOptions options)
    {
        lock (_writeLock)
        {
            try
            {
                var tempPath = _filePath + ".tmp";
                using (var stream = File.Create(tempPath))
                {
                    JsonSerializer.Serialize(stream, options, JsonOpts);
                }
                // File.Move with overwrite is the closest cross-platform atomic-rename .NET offers.
                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                LogPersistFailed(ex, _filePath);
                throw;
            }
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce — editors typically emit multiple change events on save (write, flush, attribute).
        _debounceTimer.Change(dueTime: 200, period: Timeout.Infinite);
    }

    private void DebouncedReload()
    {
        WorkbenchOptions next;
        lock (_lock)
        {
            var loaded = TryLoad();
            if (loaded == null || loaded.Equals(_current))
            {
                return;
            }
            next = loaded;
            _current = loaded;
        }
        OptionsChanged?.Invoke(next);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _debounceTimer.Dispose();
    }

    /// <summary>Default options directory: <c>%LOCALAPPDATA%\Typhon.Workbench</c> on Windows, equivalents on macOS / Linux.</summary>
    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Typhon.Workbench");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load options from {Path}; using defaults.")]
    private partial void LogLoadFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to persist options to {Path}.")]
    private partial void LogPersistFailed(Exception ex, string path);
}
