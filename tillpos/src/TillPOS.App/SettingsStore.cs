using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TillPOS.Core.Shifts;

namespace TillPOS.App;

/// <summary>Finds, imports and saves settings.json. The till's own copy lives in ProgramData; a packaged copy next to the
/// exe (zip package), else the copy built into a single-file exe (publish-field.ps1 -SingleExe), is imported on the first
/// start. A plain API secret is protected before any file is written, and is removed from every settings file the till can
/// write. (Plain .NET, so the tests compile it; DPAPI and the exe's resources are in SettingsStore.Windows.cs.)</summary>
public static partial class SettingsStore
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The till's settings: %ProgramData%\TillPOS\settings.json, or the TILLPOS_SETTINGS path.</summary>
    public static string ProgramDataPath =>
        Environment.GetEnvironmentVariable("TILLPOS_SETTINGS") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TillPOS", "settings.json");

    /// <summary>The packaged settings shipped in the zip next to TillPOS.exe.</summary>
    public static string BesideExePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    /// <summary>Loads the till's settings from <paramref name="programDataPath"/>, or imports <paramref name="besideExePath"/>,
    /// else the <paramref name="embedded"/> settings JSON (null when the exe has none), into it. Returns null when there are
    /// none. A plain secret is replaced by <paramref name="protect"/>(secret) before the settings are saved; the packaged file
    /// is then cleaned too, and a failure there is only logged. The built-in copy cannot be cleaned: it is part of the exe,
    /// so its plain secret stays readable to anyone who has the exe file (test builds only; never ship one to customers).</summary>
    public static TillSettings? Resolve(string programDataPath, string besideExePath, Func<string?> embedded, Func<string, string> protect,
        Action<TillSettings, Exception> logError)
    {
        TillSettings settings;
        if (File.Exists(programDataPath))
        {
            settings = WithDefaultDbPath(Load(programDataPath), programDataPath);
            // A plain secret beside the exe only exists in a freshly unzipped package, so it is the newest key (key rotation,
            // or recovering after a used folder was copied from another PC): adopt it.
            if (!HasPlainSecret(settings) && PackagedPlainSecret(besideExePath, programDataPath) is { } fresh)
                settings = settings with { ApiSecret = fresh };
            // The built-in secret is in the exe on every start, so it is no sign of a newer key: it is adopted only when the
            // till has no secret at all, never over a working one.
            else if (!HasPlainSecret(settings) && string.IsNullOrEmpty(settings.ApiSecretProtected) && EmbeddedPlainSecret(embedded) is { } builtIn)
                settings = settings with { ApiSecret = builtIn };
            // Counters arrived with 0.3.6: a till whose settings have none takes the package's (beside the exe, else built in),
            // so an upgraded PC gets them without deleting its settings. Counters it already has are never replaced, and an
            // empty list (every counter removed on the setup screen) stays empty.
            var adoptCounters = settings.Counters is null
                ? PackagedCounters(besideExePath, programDataPath) ?? EmbeddedCounters(embedded)
                : null;
            if (adoptCounters is not null) settings = settings with { Counters = adoptCounters };
            if (HasPlainSecret(settings) || adoptCounters is not null)
            {
                settings = ProtectSecret(settings, protect);
                Save(settings, programDataPath);
            }
        }
        else if (File.Exists(besideExePath))
        {
            settings = ProtectSecret(WithDefaultDbPath(Load(besideExePath), programDataPath), protect);
            Save(settings, programDataPath);
        }
        else if (embedded() is { } builtInJson)
        {
            // Imported exactly like a packaged file beside the exe.
            settings = ProtectSecret(WithDefaultDbPath(Parse(builtInJson, BuiltInName), programDataPath), protect);
            Save(settings, programDataPath);
        }
        else
        {
            return null;
        }

        RemovePlainSecret(besideExePath, programDataPath, protect, settings, logError);
        return settings;
    }

    /// <summary>Reads settings.json. JSON nulls in the text settings become "" (the till treats blank as "not set").</summary>
    public static TillSettings Load(string path) => Parse(File.ReadAllText(path), path);

    /// <summary>Settings JSON text; <paramref name="source"/> names it in errors.</summary>
    private static TillSettings Parse(string json, string source)
    {
        var s = JsonSerializer.Deserialize<TillSettings>(json, ReadOptions) ?? throw new InvalidDataException($"{source} is empty.");
        return s with
        {
            BaseUrl = s.BaseUrl ?? "",
            ApiKey = s.ApiKey ?? "",
            ApiSecretProtected = s.ApiSecretProtected ?? "",
            PosProfile = s.PosProfile ?? "",
            CashMode = s.CashMode ?? "",
            CardMode = s.CardMode ?? "",
            PrinterName = s.PrinterName ?? "",
            DbPath = s.DbPath ?? "",
        };
    }

    /// <summary>Writes indented JSON to a temp file next to the target, then swaps it in, so a crash or power cut never
    /// leaves a half-written settings.json. An existing file is replaced in place (File.Replace keeps its permissions).
    /// On failure the temp file is removed.</summary>
    public static void Save(TillSettings settings, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, WriteOptions));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception)
            {
                // Best effort; the original error matters more.
            }
            throw;
        }
    }

    private const string BuiltInName = "The settings built into TillPOS.exe";

    private static bool HasPlainSecret(TillSettings settings) => !string.IsNullOrEmpty(settings.ApiSecret);

    private static bool IsSameFile(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The plain secret of the packaged file, if it is a different file and has one. An unreadable packaged file
    /// counts as none here (RemovePlainSecret logs the problem).</summary>
    private static string? PackagedPlainSecret(string besideExePath, string programDataPath)
    {
        try
        {
            if (!File.Exists(besideExePath) || IsSameFile(besideExePath, programDataPath)) return null;
            var packaged = Load(besideExePath);
            return HasPlainSecret(packaged) ? packaged.ApiSecret : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The packaged file's counters, if it is a different file and has some (unreadable = none).</summary>
    private static IReadOnlyList<CounterSettings>? PackagedCounters(string besideExePath, string programDataPath)
    {
        try
        {
            if (!File.Exists(besideExePath) || IsSameFile(besideExePath, programDataPath)) return null;
            return Load(besideExePath).Counters is { Count: > 0 } counters ? counters : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The built-in settings' counters, if any (unreadable = none).</summary>
    private static IReadOnlyList<CounterSettings>? EmbeddedCounters(Func<string?> embedded)
    {
        try
        {
            return embedded() is { } json && Parse(json, BuiltInName).Counters is { Count: > 0 } counters ? counters : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The plain secret of the built-in settings, if any. Unreadable built-in settings count as none here.</summary>
    private static string? EmbeddedPlainSecret(Func<string?> embedded)
    {
        try
        {
            if (embedded() is not { } json) return null;
            var builtIn = Parse(json, BuiltInName);
            return HasPlainSecret(builtIn) ? builtIn.ApiSecret : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static TillSettings ProtectSecret(TillSettings settings, Func<string, string> protect) =>
        HasPlainSecret(settings) ? settings with { ApiSecretProtected = protect(settings.ApiSecret!), ApiSecret = null } : settings;

    /// <summary>A blank DbPath means "the default": till.db next to the till's settings file.</summary>
    private static TillSettings WithDefaultDbPath(TillSettings settings, string programDataPath) =>
        string.IsNullOrWhiteSpace(settings.DbPath)
            ? settings with { DbPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(programDataPath))!, "till.db") }
            : settings;

    /// <summary>The packaged file keeps its own secret, protected. The zip folder may be read-only (e.g. under Program Files);
    /// the till still works, so failures are only logged (the message never contains the secret).</summary>
    private static void RemovePlainSecret(string besideExePath, string programDataPath, Func<string, string> protect, TillSettings settings,
        Action<TillSettings, Exception> logError)
    {
        try
        {
            if (!File.Exists(besideExePath)) return;
            if (IsSameFile(besideExePath, programDataPath)) return;
            var packaged = Load(besideExePath);
            if (HasPlainSecret(packaged)) Save(ProtectSecret(packaged, protect), besideExePath);
        }
        catch (Exception ex)
        {
            logError(settings, new IOException($"The plain API secret could not be removed from {besideExePath}: {ex.GetType().Name}: {ex.Message}"));
        }
    }
}
