using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TillPOS.App;

/// <summary>Finds, imports and saves settings.json. The till's own copy lives in ProgramData; a packaged copy next to the
/// exe is imported on the first start, and a plain API secret in either is replaced by a DPAPI-protected one.</summary>
public static class SettingsStore
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

    private static string ProgramDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TillPOS");

    /// <summary>The till's settings: %ProgramData%\TillPOS\settings.json, or the TILLPOS_SETTINGS path.</summary>
    public static string ProgramDataPath =>
        Environment.GetEnvironmentVariable("TILLPOS_SETTINGS") is { Length: > 0 } custom ? custom : Path.Combine(ProgramDataFolder, "settings.json");

    /// <summary>The packaged settings shipped in the zip next to TillPOS.exe.</summary>
    public static string BesideExePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    /// <summary>Loads the till's settings, importing the packaged file and protecting a plain secret on the way.
    /// Returns null when there is no settings file at all. Problems rewriting the packaged file are only logged.</summary>
    public static TillSettings? Resolve(Action<TillSettings, Exception> logError)
    {
        TillSettings settings;
        if (File.Exists(ProgramDataPath))
        {
            settings = Load(ProgramDataPath);
        }
        else if (File.Exists(BesideExePath))
        {
            settings = Load(BesideExePath);
            if (string.IsNullOrWhiteSpace(settings.DbPath)) settings = settings with { DbPath = Path.Combine(ProgramDataFolder, "till.db") };
            Save(settings, ProgramDataPath);
        }
        else
        {
            return null;
        }

        if (string.IsNullOrEmpty(settings.ApiSecret)) return settings;

        var protectedSecret = SecretProtector.Protect(settings.ApiSecret);
        settings = settings with { ApiSecretProtected = protectedSecret, ApiSecret = null };
        Save(settings, ProgramDataPath);
        RemovePlainSecretBesideExe(protectedSecret, settings, logError);
        return settings;
    }

    public static TillSettings Load(string path) =>
        JsonSerializer.Deserialize<TillSettings>(File.ReadAllText(path), ReadOptions) ?? throw new InvalidDataException($"{path} is empty.");

    /// <summary>Writes indented JSON to a temp file next to the target, then replaces the target, so a crash or power cut
    /// never leaves a half-written settings.json.</summary>
    public static void Save(TillSettings settings, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, WriteOptions));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The zip folder may be read-only (e.g. under Program Files); the till still works, so failures are only logged.</summary>
    private static void RemovePlainSecretBesideExe(string protectedSecret, TillSettings settings, Action<TillSettings, Exception> logError)
    {
        try
        {
            if (!File.Exists(BesideExePath)) return;
            if (string.Equals(Path.GetFullPath(BesideExePath), Path.GetFullPath(ProgramDataPath), StringComparison.OrdinalIgnoreCase)) return;
            var packaged = Load(BesideExePath);
            if (string.IsNullOrEmpty(packaged.ApiSecret)) return;
            Save(packaged with { ApiSecretProtected = protectedSecret, ApiSecret = null }, BesideExePath);
        }
        catch (Exception ex)
        {
            logError(settings, new IOException($"The plain API secret could not be removed from {BesideExePath}: {ex.Message}", ex));
        }
    }
}
