namespace TillPOS.App;

public static partial class SettingsStore
{
    /// <summary>The till's real settings files, with the secret protected by DPAPI for this PC.</summary>
    public static TillSettings? Resolve(Action<TillSettings, Exception> logError) =>
        Resolve(ProgramDataPath, BesideExePath, SecretProtector.Protect, logError);
}
