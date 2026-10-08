using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TillPOS.Sync.Upload;
using TillPOS.Core.Shifts;

namespace TillPOS.App;

/// <summary>The till must not start with these settings (e.g. a Dev build on Production settings); the message says why and what
/// to do, and is shown as it is.</summary>
public sealed class SettingsRefusedException(string message) : Exception(message);

/// <summary>Finds, imports and saves settings.json. The till's own copy lives in ProgramData; a packaged copy next to the
/// exe (zip package), else the copy built into a single-file exe (publish-field.ps1 -SingleExe), is imported on the first
/// start. A plain API secret is protected before any file is written, and is removed from every settings file the till can
/// write. (Plain .NET, so the tests compile it; DPAPI and the exe's resources are in SettingsStore.Windows.cs.)</summary>
public static partial class SettingsStore
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new TolerantUploadModeConverter(), new JsonStringEnumConverter() },
    };

    /// <summary>Reads the upload mode leniently: an unknown name or number is Off (never a reason to refuse the settings, and
    /// never a way to end up Live).</summary>
    private sealed class TolerantUploadModeConverter : JsonConverter<UploadMode>
    {
        public override UploadMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.String when Enum.TryParse<UploadMode>(reader.GetString(), ignoreCase: true, out var mode)
                && Enum.IsDefined(mode) && !int.TryParse(reader.GetString(), out _) => mode,
            JsonTokenType.Number when reader.TryGetInt32(out var n) && Enum.IsDefined((UploadMode)n) => (UploadMode)n,
            _ => UploadMode.Off,
        };

        public override void Write(Utf8JsonWriter writer, UploadMode value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The data folder of a Production build, and of a Dev build (so a dev build never mixes with production data).</summary>
    public const string ProductionFolder = "TillPOS";
    public const string DevFolder = "TillPOS-Dev";

    /// <summary>The till's settings file: <paramref name="overridePath"/> (TILLPOS_SETTINGS) when set, otherwise settings.json in
    /// the environment's folder under <paramref name="commonAppData"/> (%ProgramData%\TillPOS, or %ProgramData%\TillPOS-Dev for a
    /// Dev build).</summary>
    public static string SettingsPath(string? overridePath, string commonAppData, bool dev) =>
        !string.IsNullOrEmpty(overridePath)
            ? overridePath
            : Path.Combine(commonAppData, dev ? DevFolder : ProductionFolder, "settings.json");

    /// <summary>True when the package this exe came with is a Dev build: the settings.json beside the exe when there is one,
    /// else the built-in settings (the same order as the import). Missing or unreadable settings are not Dev.</summary>
    public static bool PackagedIsDev(string besideExePath, Func<string?> embedded)
    {
        try
        {
            if (File.Exists(besideExePath)) return IsDevJson(File.ReadAllText(besideExePath));
            return embedded() is { } json && IsDevJson(json);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsDevJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (!string.Equals(property.Name, nameof(TillSettings.Environment), StringComparison.OrdinalIgnoreCase)) continue;
            return property.Value.ValueKind == JsonValueKind.String
                && string.Equals(property.Value.GetString()?.Trim(), TillSettings.DevEnvironment, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>The packaged settings shipped in the zip next to TillPOS.exe.</summary>
    public static string BesideExePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    /// <summary>Loads the till's settings from <paramref name="programDataPath"/>, or imports <paramref name="besideExePath"/>,
    /// else the <paramref name="embedded"/> settings JSON (null when the exe has none), into it. Returns null when there are
    /// none. A plain secret is replaced by <paramref name="protect"/>(secret) before the settings are saved; the packaged file
    /// is then cleaned too, and a failure there is only logged. The built-in copy cannot be cleaned: it is part of the exe,
    /// so its plain secret stays readable to anyone who has the exe file (test builds only; never ship one to customers).</summary>
    /// <exception cref="SettingsRefusedException">A packaged settings file (beside the exe, or built in) is for the other
    /// environment than the settings in use (Dev vs Production), or an Environment is neither "Dev" nor "Production". Nothing is
    /// adopted or written then.</exception>
    public static TillSettings? Resolve(string programDataPath, string besideExePath, Func<string?> embedded, Func<string, string> protect,
        Action<TillSettings, Exception> logError)
    {
        var packages = PackagedEnvironments(besideExePath, programDataPath, embedded);
        TillSettings settings;
        if (File.Exists(programDataPath))
        {
            settings = WithDefaultDbPath(Load(programDataPath), programDataPath);
            // Before anything is adopted from a package: a package of the other environment never lends its secret or counters.
            RefuseOtherEnvironment(packages, settings, programDataPath);
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
            var packaged = Load(besideExePath);
            RefuseOtherEnvironment(packages, packaged, besideExePath);
            settings = ProtectSecret(WithDefaultDbPath(packaged, programDataPath), protect);
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

    /// <summary>The environment of each packaged settings file there is: the one beside the exe (unless it is the till's own file)
    /// and the built-in one. An unreadable package counts as none (as everywhere else); an unknown Environment refuses.</summary>
    private static List<(string Environment, string Source)> PackagedEnvironments(string besideExePath, string programDataPath,
        Func<string?> embedded)
    {
        var found = new List<(string, string)>();
        if (File.Exists(besideExePath) && !IsSameFile(besideExePath, programDataPath)
            && RawEnvironment(() => File.ReadAllText(besideExePath)) is { } beside)
            found.Add((Canonical(beside.Value, besideExePath), besideExePath));
        if (RawEnvironment(embedded) is { } builtIn) found.Add((Canonical(builtIn.Value, BuiltInName), BuiltInName));
        return found;
    }

    /// <summary>The Environment written in the settings JSON (Value null when missing or JSON null), or null when there is no JSON
    /// or it cannot be read.</summary>
    private static (string? Value, bool Found)? RawEnvironment(Func<string?> json)
    {
        try
        {
            if (json() is not { } text) return null;
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, nameof(TillSettings.Environment), StringComparison.OrdinalIgnoreCase)) continue;
                return property.Value.ValueKind switch
                {
                    JsonValueKind.Null => (null, true),
                    JsonValueKind.String => (property.Value.GetString(), true),
                    _ => (property.Value.GetRawText(), true),
                };
            }
            return (null, false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>"Dev" or "Production" (any case, spaces ignored); missing (null) is Production, settings from before environments.</summary>
    /// <exception cref="SettingsRefusedException">Any other value.</exception>
    private static string Canonical(string? environment, string source)
    {
        if (environment is null) return TillSettings.ProductionEnvironment;
        var trimmed = environment.Trim();
        if (string.Equals(trimmed, TillSettings.DevEnvironment, StringComparison.OrdinalIgnoreCase)) return TillSettings.DevEnvironment;
        if (string.Equals(trimmed, TillSettings.ProductionEnvironment, StringComparison.OrdinalIgnoreCase)) return TillSettings.ProductionEnvironment;
        throw new SettingsRefusedException(
            $"{source} has Environment \"{environment}\"; it must be \"Dev\" or \"Production\". Use the matching TillPOS build, or correct that settings file.");
    }

    private static void RefuseOtherEnvironment(List<(string Environment, string Source)> packages, TillSettings settings, string path)
    {
        foreach (var (environment, _) in packages)
            if (environment != settings.Environment)
                throw new SettingsRefusedException($"This TillPOS is a {environment} build but its settings at {path} are for " +
                    $"{settings.Environment}. Use the matching build, or remove that settings file.");
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
            Environment = Canonical(s.Environment, source),
            // A test build (local test cashiers or the sample QR) never runs Live.
            Upload = s.EffectiveUpload,
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

    /// <summary>A blank DbPath means "the default": till.db next to the till's settings file. A Dev build whose DbPath is in the
    /// production data folder (e.g. the record's default, when the file has no DbPath) also uses that default, so a dev build
    /// never writes to production data.</summary>
    private static TillSettings WithDefaultDbPath(TillSettings settings, string programDataPath) =>
        string.IsNullOrWhiteSpace(settings.DbPath) || (settings.IsDev && InProductionFolder(settings.DbPath))
            ? settings with { DbPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(programDataPath))!, "till.db") }
            : settings;

    /// <summary>The path is directly in the production data folder (C:\ProgramData\TillPOS or %ProgramData%\TillPOS).</summary>
    private static bool InProductionFolder(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path.Trim()));
            return new[]
            {
                Path.Combine(@"C:\ProgramData", ProductionFolder),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductionFolder),
            }.Any(production => string.Equals(folder, Path.GetFullPath(production), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

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
