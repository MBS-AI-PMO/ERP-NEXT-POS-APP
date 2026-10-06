using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TillPOS.App;

/// <summary>Finds, imports and saves settings.json. The till's own copy lives in ProgramData; a packaged copy next to the
/// exe is imported on the first start. A plain API secret is protected before any file is written, and is removed from
/// every settings file the till can write. (Plain .NET, so the tests compile it; DPAPI is in SettingsStore.Windows.cs.)</summary>
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

    /// <summary>Loads the till's settings from <paramref name="programDataPath"/>, or imports <paramref name="besideExePath"/>
    /// into it. Returns null when neither exists. A plain secret is replaced by <paramref name="protect"/>(secret) before the
    /// settings are saved; the packaged file is then cleaned too, and a failure there is only logged.</summary>
    public static TillSettings? Resolve(string programDataPath, string besideExePath, Func<string, string> protect,
        Action<TillSettings, Exception> logError)
    {
        TillSettings settings;
        if (File.Exists(programDataPath))
        {
            settings = WithDefaultDbPath(Load(programDataPath), programDataPath);
            if (HasPlainSecret(settings))
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
        else
        {
            return null;
        }

        RemovePlainSecret(besideExePath, programDataPath, protect, settings, logError);
        return settings;
    }

    /// <summary>Reads settings.json. JSON nulls in the text settings become "" (the till treats blank as "not set").</summary>
    public static TillSettings Load(string path)
    {
        var s = JsonSerializer.Deserialize<TillSettings>(File.ReadAllText(path), ReadOptions) ?? throw new InvalidDataException($"{path} is empty.");
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

    private static bool HasPlainSecret(TillSettings settings) => !string.IsNullOrEmpty(settings.ApiSecret);

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
            if (string.Equals(Path.GetFullPath(besideExePath), Path.GetFullPath(programDataPath), StringComparison.OrdinalIgnoreCase)) return;
            var packaged = Load(besideExePath);
            if (HasPlainSecret(packaged)) Save(ProtectSecret(packaged, protect), besideExePath);
        }
        catch (Exception ex)
        {
            logError(settings, new IOException($"The plain API secret could not be removed from {besideExePath}: {ex.GetType().Name}: {ex.Message}"));
        }
    }
}
