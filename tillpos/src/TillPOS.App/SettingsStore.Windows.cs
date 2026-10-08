using System.IO;
using System.Reflection;

namespace TillPOS.App;

public static partial class SettingsStore
{
    /// <summary>The manifest resource name of the settings built into a single-file exe (TillPOS.App.csproj, PackagedSettings).</summary>
    public const string EmbeddedSettingsName = "packaged-settings.json";

    private static readonly Lazy<string> programDataPath = new(() => SettingsPath(Environment.GetEnvironmentVariable("TILLPOS_SETTINGS"),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), PackagedIsDev(BesideExePath, ReadEmbeddedSettings)));

    /// <summary>The till's settings: the TILLPOS_SETTINGS path, else %ProgramData%\TillPOS\settings.json, or
    /// %ProgramData%\TillPOS-Dev\settings.json when the package (beside the exe, else built in) is a Dev build. Worked out once
    /// per run, so every screen reads and writes the same file.</summary>
    public static string ProgramDataPath => programDataPath.Value;

    /// <summary>The till's real settings files (and the exe's built-in settings), with the secret protected by DPAPI for this PC.</summary>
    public static TillSettings? Resolve(Action<TillSettings, Exception> logError) =>
        Resolve(ProgramDataPath, BesideExePath, ReadEmbeddedSettings, SecretProtector.Protect, logError);

    /// <summary>The settings JSON built into TillPOS.exe (publish-field.ps1 -SingleExe), or null for a normal build.
    /// Unlike settings.json beside the exe it cannot be scrubbed after import: it is inside the exe, so its plain secret
    /// stays there for anyone who has the file.</summary>
    private static string? ReadEmbeddedSettings()
    {
        using var stream = Assembly.GetEntryAssembly()!.GetManifestResourceStream(EmbeddedSettingsName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
