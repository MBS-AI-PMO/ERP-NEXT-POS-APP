using System.Text.Json;
using TillPOS.App;
using TillPOS.Sync.Upload;

namespace TillPOS.Tests.App;

public sealed class SettingsStoreTests : IDisposable
{
    private const string PlainSecret = "s3cr3t-plain-value";
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tillpos-settings-{Guid.NewGuid():N}");
    private readonly string programData;
    private readonly string besideExe;
    private readonly List<Exception> logged = [];
    private string? embedded;   // the settings built into a single-file exe (publish-field.ps1 -SingleExe), if any

    public SettingsStoreTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "zip"));
        programData = Path.Combine(root, "ProgramData", "TillPOS", "settings.json");
        besideExe = Path.Combine(root, "zip", "settings.json");
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    private static string Protect(string s) => "P(" + s + ")";

    private TillSettings? Resolve() => SettingsStore.Resolve(programData, besideExe, () => embedded, Protect, (_, ex) => logged.Add(ex));

    private static void Write(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private static string Json(string secretField, string dbPath = "C:/ProgramData/TillPOS/till.db") =>
        $$"""{ "BaseUrl": "https://erp.example", "ApiKey": "key1", {{secretField}}, "PosProfile": "Till 1", "TillNumber": 1, "DbPath": "{{dbPath}}" }""";

    private static bool HasProperty(string path, string name) =>
        JsonDocument.Parse(File.ReadAllText(path)).RootElement.TryGetProperty(name, out _);

    [Fact]
    public void A_packaged_plain_secret_is_protected_before_anything_is_written()
    {
        Write(besideExe, Json($"\"ApiSecret\": \"{PlainSecret}\""));

        var settings = Resolve()!;

        Assert.Equal($"P({PlainSecret})", settings.ApiSecretProtected);
        Assert.Null(settings.ApiSecret);
        foreach (var path in new[] { programData, besideExe })
        {
            var text = File.ReadAllText(path);
            Assert.DoesNotContain(PlainSecret, text.Replace($"P({PlainSecret})", ""));
            Assert.False(HasProperty(path, "ApiSecret"), path);
            Assert.Equal($"P({PlainSecret})", JsonDocument.Parse(text).RootElement.GetProperty("ApiSecretProtected").GetString());
        }
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
        Assert.Empty(logged);
    }

    [Fact]
    public void A_freshly_unzipped_package_secret_replaces_the_installed_one()
    {
        Write(programData, Json("\"ApiSecretProtected\": \"P(old)\""));
        Write(besideExe, Json("\"ApiSecret\": \"new\""));

        var settings = Resolve()!;

        Assert.Equal("P(new)", settings.ApiSecretProtected);
        Assert.Null(settings.ApiSecret);
        foreach (var path in new[] { programData, besideExe })
        {
            var text = File.ReadAllText(path);
            Assert.Equal("P(new)", JsonDocument.Parse(text).RootElement.GetProperty("ApiSecretProtected").GetString());
            Assert.False(HasProperty(path, "ApiSecret"), path);
            Assert.DoesNotContain("\"new\"", text);
            Assert.DoesNotContain("P(old)", text);
        }
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void An_already_protected_package_leaves_the_installed_settings_alone()
    {
        Write(programData, Json("\"ApiSecretProtected\": \"P(installed)\""));
        var before = File.ReadAllText(programData);
        Write(besideExe, Json("\"ApiSecretProtected\": \"P(packaged)\""));
        var packagedBefore = File.ReadAllText(besideExe);

        var settings = Resolve()!;

        Assert.Equal("P(installed)", settings.ApiSecretProtected);
        Assert.Equal(before, File.ReadAllText(programData));
        Assert.Equal(packagedBefore, File.ReadAllText(besideExe));
    }

    [Fact]
    public void A_read_only_packaged_file_does_not_stop_the_start()
    {
        Write(besideExe, Json($"\"ApiSecret\": \"{PlainSecret}\""));
        File.SetAttributes(besideExe, FileAttributes.ReadOnly);

        var settings = Resolve()!;

        Assert.Equal($"P({PlainSecret})", settings.ApiSecretProtected);
        Assert.False(HasProperty(programData, "ApiSecret"));
        Assert.Equal($"P({PlainSecret})", JsonDocument.Parse(File.ReadAllText(programData)).RootElement.GetProperty("ApiSecretProtected").GetString());
        Assert.Single(logged);
        Assert.DoesNotContain(PlainSecret, logged[0].ToString());
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_blank_db_path_on_import_goes_next_to_the_programdata_settings()
    {
        Write(besideExe, Json("\"ApiSecretProtected\": \"P(x)\"", dbPath: ""));

        var settings = Resolve()!;

        Assert.Equal(Path.Combine(Path.GetDirectoryName(programData)!, "till.db"), settings.DbPath);
        Assert.Equal(settings.DbPath, SettingsStore.Load(programData).DbPath);
    }

    [Fact]
    public void No_settings_file_anywhere_returns_null() => Assert.Null(Resolve());

    [Fact]
    public void Built_in_settings_alone_are_imported_with_the_secret_protected()
    {
        embedded = Json($"\"ApiSecret\": \"{PlainSecret}\", \"SampleQr\": true", dbPath: "");

        var settings = Resolve()!;

        Assert.Equal($"P({PlainSecret})", settings.ApiSecretProtected);
        Assert.Null(settings.ApiSecret);
        Assert.True(settings.SampleQr);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(programData)!, "till.db"), settings.DbPath);
        var text = File.ReadAllText(programData);
        Assert.DoesNotContain(PlainSecret, text.Replace($"P({PlainSecret})", ""));
        Assert.False(HasProperty(programData, "ApiSecret"));
        Assert.Equal($"P({PlainSecret})", JsonDocument.Parse(text).RootElement.GetProperty("ApiSecretProtected").GetString());
        Assert.True(SettingsStore.Load(programData).SampleQr);
        Assert.False(File.Exists(besideExe));
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
        Assert.Empty(logged);
    }

    [Fact]
    public void Built_in_settings_never_override_a_working_installed_secret()
    {
        Write(programData, Json("\"ApiSecretProtected\": \"P(installed)\""));
        var before = File.ReadAllText(programData);
        embedded = Json("\"ApiSecret\": \"built-in\", \"SampleQr\": true");

        var settings = Resolve()!;

        Assert.Equal("P(installed)", settings.ApiSecretProtected);
        Assert.False(settings.SampleQr);
        Assert.Equal(before, File.ReadAllText(programData));
    }

    [Fact]
    public void Built_in_secret_is_adopted_when_the_installed_settings_have_no_secret_at_all()
    {
        Write(programData, Json("\"ApiSecretProtected\": \"\""));
        embedded = Json("\"ApiSecret\": \"built-in\"");

        var settings = Resolve()!;

        Assert.Equal("P(built-in)", settings.ApiSecretProtected);
        Assert.Null(settings.ApiSecret);
        Assert.Equal("P(built-in)", SettingsStore.Load(programData).ApiSecretProtected);
        Assert.False(HasProperty(programData, "ApiSecret"));
    }

    [Fact]
    public void A_packaged_file_beside_the_exe_comes_before_the_built_in_settings()
    {
        Write(besideExe, Json("\"ApiSecret\": \"beside\""));
        embedded = Json("\"ApiSecret\": \"built-in\"");

        Assert.Equal("P(beside)", Resolve()!.ApiSecretProtected);
        Assert.Equal("P(beside)", SettingsStore.Load(programData).ApiSecretProtected);
    }

    [Fact]
    public void Null_strings_in_the_file_load_as_empty()
    {
        Write(programData, """{ "BaseUrl": "https://erp.example", "ApiKey": "k", "ApiSecretProtected": "P(x)", "PrinterName": null, "CashMode": null }""");

        var settings = SettingsStore.Load(programData);

        Assert.Equal("", settings.PrinterName);
        Assert.Equal("", settings.CashMode);
    }

    [Fact]
    public void Packaged_counters_are_imported_and_saved_with_their_four_fields()
    {
        Write(besideExe, """
            { "BaseUrl": "https://erp.example", "ApiKey": "k", "ApiSecret": "s", "PosProfile": "Test Counter", "TillNumber": 1,
              "CashMode": "Cash Counter 2", "CardMode": "Credit Card",
              "Counters": [
                { "PosProfile": "Test Counter", "Label": "Test Counter", "CashMode": "Cash Counter 2", "CardMode": "Credit Card" },
                { "PosProfile": "Al Ain Counter 1", "Label": "Counter 1", "CashMode": "Cash Counter 1", "CardMode": "Credit Card" }
              ] }
            """);

        var settings = Resolve()!;

        Assert.Equal(["Test Counter", "Al Ain Counter 1"], settings.EffectiveCounters().Select(c => c.PosProfile));
        Assert.Equal("Cash Counter 1", settings.EffectiveCounters()[1].CashMode);
        var saved = JsonDocument.Parse(File.ReadAllText(programData)).RootElement.GetProperty("Counters");
        Assert.Equal(2, saved.GetArrayLength());
        Assert.Equal(["PosProfile", "Label", "CashMode", "CardMode"], saved[1].EnumerateObject().Select(p => p.Name));
        Assert.Equal("Counter 1", SettingsStore.Load(programData).Counters![1].Label);
    }

    [Fact]
    public void Settings_without_counters_still_load_and_are_saved_without_them()
    {
        Write(programData, """{ "BaseUrl": "https://erp.example", "ApiKey": "k", "ApiSecretProtected": "P(x)", "PosProfile": "Test Counter", "CashMode": "Cash Counter 2" }""");

        var settings = Resolve()!;

        Assert.Null(settings.Counters);
        Assert.Equal("Test Counter", Assert.Single(settings.EffectiveCounters()).PosProfile);
        SettingsStore.Save(settings, programData);
        Assert.False(HasProperty(programData, "Counters"));
    }

    [Fact]
    public void Upload_mode_is_read_and_written_as_text_and_missing_means_off()
    {
        Write(programData, Json("\"ApiSecretProtected\": \"P(x)\""));
        Assert.Equal(UploadMode.Off, SettingsStore.Load(programData).Upload);

        Write(programData, Json("\"ApiSecretProtected\": \"P(x)\", \"Upload\": \"DryRun\""));
        var settings = SettingsStore.Load(programData);
        Assert.Equal(UploadMode.DryRun, settings.Upload);

        SettingsStore.Save(settings with { Upload = UploadMode.Live }, programData);
        Assert.Equal("Live", JsonDocument.Parse(File.ReadAllText(programData)).RootElement.GetProperty("Upload").GetString());
        Assert.Equal(UploadMode.Live, SettingsStore.Load(programData).Upload);
    }
}
