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

    private const string PackagedCountersJson = """
        { "BaseUrl": "https://erp.example", "ApiKey": "k", "PosProfile": "Test Counter", "TillNumber": 1,
          "Counters": [
            { "PosProfile": "Test Counter", "Label": "Test Counter", "CashMode": "Cash Counter 2", "CardMode": "Credit Card" },
            { "PosProfile": "Al Ain Counter 1", "Label": "Counter 1", "CashMode": "Cash Counter 1", "CardMode": "Credit Card" }
          ] }
        """;

    private const string OldTillJson =
        """{ "BaseUrl": "https://erp.example", "ApiKey": "k", "ApiSecretProtected": "P(x)", "PosProfile": "Test Counter", "TillNumber": 3, "PrinterName": "EPSON" }""";

    [Fact]
    public void An_upgraded_till_without_counters_adopts_the_packaged_ones_and_keeps_its_own_settings()
    {
        Write(programData, OldTillJson);
        Write(besideExe, PackagedCountersJson);

        var settings = Resolve()!;

        Assert.Equal(["Test Counter", "Al Ain Counter 1"], settings.Counters!.Select(c => c.PosProfile));
        Assert.Equal((3, "EPSON", "P(x)"), (settings.TillNumber, settings.PrinterName, settings.ApiSecretProtected));
        Assert.Equal(2, SettingsStore.Load(programData).Counters!.Count);             // saved, so it happens once
    }

    [Fact]
    public void An_upgraded_till_adopts_the_built_in_counters_when_there_is_no_packaged_file()
    {
        Write(programData, OldTillJson);
        embedded = PackagedCountersJson;

        Assert.Equal(2, Resolve()!.Counters!.Count);
    }

    [Fact]
    public void Counters_the_till_has_or_removed_are_never_replaced_by_the_package()
    {
        Write(besideExe, PackagedCountersJson);
        Write(programData, OldTillJson.Replace("\"TillNumber\": 3", "\"TillNumber\": 3, \"Counters\": [ { \"PosProfile\": \"Al Ain Counter 2\", \"CashMode\": \"Cash Counter 2\" } ]"));
        Assert.Equal("Al Ain Counter 2", Assert.Single(Resolve()!.Counters!).PosProfile);

        Write(programData, OldTillJson.Replace("\"TillNumber\": 3", "\"TillNumber\": 3, \"Counters\": []"));
        Assert.Empty(Resolve()!.Counters!);
    }

    [Theory]
    [InlineData("\"Banana\"", UploadMode.Off)]
    [InlineData("7", UploadMode.Off)]
    [InlineData("\"2\"", UploadMode.Off)]
    [InlineData("null", UploadMode.Off)]
    [InlineData("\"dryrun\"", UploadMode.DryRun)]
    [InlineData("\"Live\"", UploadMode.Live)]
    [InlineData("1", UploadMode.DryRun)]
    public void An_unknown_upload_mode_reads_as_off(string value, UploadMode expected)
    {
        Write(programData, Json($"\"ApiSecretProtected\": \"P(x)\", \"Upload\": {value}"));
        Assert.Equal(expected, SettingsStore.Load(programData).Upload);
    }

    [Fact]
    public void A_test_build_never_loads_as_live()
    {
        Write(programData, Json("\"ApiSecretProtected\": \"P(x)\", \"Upload\": \"Live\", \"SampleQr\": true"));
        Assert.Equal(UploadMode.Off, SettingsStore.Load(programData).Upload);

        Write(programData, Json("\"ApiSecretProtected\": \"P(x)\", \"Upload\": \"DryRun\", \"LocalTestCashiers\": [{\"Id\":\"c\",\"Name\":\"C\",\"Pin\":\"1234\",\"IsSupervisor\":false}]"));
        Assert.Equal(UploadMode.DryRun, SettingsStore.Load(programData).Upload);
    }

    // ---- Environments: a Dev build keeps its own data folder ----

    private const string DevJson = """{ "BaseUrl": "https://dev.quickgroc.com/", "ApiKey": "k", "ApiSecret": "dev-secret", "Environment": "Dev", "Upload": "Live", "SampleQr": true }""";

    [Fact]
    public void The_settings_path_is_per_environment_and_tillpos_settings_overrides_it()
    {
        var common = Path.Combine(root, "PD");
        Assert.Equal(Path.Combine(common, "TillPOS", "settings.json"), SettingsStore.SettingsPath(null, common, dev: false));
        Assert.Equal(Path.Combine(common, "TillPOS-Dev", "settings.json"), SettingsStore.SettingsPath("", common, dev: true));
        Assert.Equal(@"X:\custom\settings.json", SettingsStore.SettingsPath(@"X:\custom\settings.json", common, dev: true));
        Assert.Equal(@"X:\custom\settings.json", SettingsStore.SettingsPath(@"X:\custom\settings.json", common, dev: false));
    }

    [Fact]
    public void A_dev_package_beside_the_exe_or_built_in_is_recognised()
    {
        Assert.False(SettingsStore.PackagedIsDev(besideExe, () => embedded));

        embedded = DevJson;
        Assert.True(SettingsStore.PackagedIsDev(besideExe, () => embedded));

        // The packaged file beside the exe comes first, as on import.
        Write(besideExe, Json("\"ApiSecret\": \"x\""));
        Assert.False(SettingsStore.PackagedIsDev(besideExe, () => embedded));

        Write(besideExe, DevJson.Replace("\"Dev\"", "\"dev\""));
        Assert.True(SettingsStore.PackagedIsDev(besideExe, () => null));
    }

    [Fact]
    public void Unreadable_packaged_settings_are_not_dev()
    {
        Write(besideExe, "{ not json");
        Assert.False(SettingsStore.PackagedIsDev(besideExe, () => DevJson));
        File.Delete(besideExe);
        Assert.False(SettingsStore.PackagedIsDev(besideExe, () => "{ broken"));
        Assert.False(SettingsStore.PackagedIsDev(besideExe, () => throw new IOException("no resource")));
    }

    [Fact]
    public void A_dev_build_keeps_its_database_next_to_its_own_settings()
    {
        var devSettings = Path.Combine(root, "ProgramData", "TillPOS-Dev", "settings.json");
        embedded = DevJson;   // no DbPath at all: the record's default would be the production folder

        var settings = SettingsStore.Resolve(devSettings, besideExe, () => embedded, Protect, (_, ex) => logged.Add(ex))!;

        Assert.Equal(Path.Combine(root, "ProgramData", "TillPOS-Dev", "till.db"), settings.DbPath);
        Assert.True(settings.IsDev);
        Assert.Equal(UploadMode.Live, settings.Upload);
        // The next start reads the saved file (no DbPath written for the default? either way it stays in the Dev folder).
        Assert.Equal(Path.Combine(root, "ProgramData", "TillPOS-Dev", "till.db"),
            SettingsStore.Resolve(devSettings, besideExe, () => embedded, Protect, (_, ex) => logged.Add(ex))!.DbPath);
    }

    [Fact]
    public void A_dev_settings_file_pointing_at_the_production_database_is_moved_to_its_own_folder()
    {
        var devSettings = Path.Combine(root, "ProgramData", "TillPOS-Dev", "settings.json");
        Write(devSettings, DevJson.Replace("\"ApiSecret\": \"dev-secret\"", "\"ApiSecretProtected\": \"P(x)\", \"DbPath\": \"c:\\\\programdata\\\\TillPOS\\\\till.db\""));

        var settings = SettingsStore.Resolve(devSettings, besideExe, () => null, Protect, (_, ex) => logged.Add(ex))!;

        Assert.Equal(Path.Combine(root, "ProgramData", "TillPOS-Dev", "till.db"), settings.DbPath);
    }

    [Fact]
    public void A_production_build_keeps_its_configured_database()
    {
        Write(programData, Json("\"ApiSecretProtected\": \"P(x)\"", dbPath: "C:/ProgramData/TillPOS/till.db"));
        Assert.Equal("C:/ProgramData/TillPOS/till.db", Resolve()!.DbPath);
    }

    // ---- A Dev build never runs on Production settings, or the other way round ----

    private static string EnvJson(string? environment, string secretField = "\"ApiSecretProtected\": \"P(installed)\"") =>
        $$"""{ "BaseUrl": "https://erp.example", "ApiKey": "key1", {{secretField}}, "PosProfile": "Till 1"{{(environment is null ? "" : $", \"Environment\": \"{environment}\"")}} }""";

    [Theory]
    [InlineData("Dev", "Production")]
    [InlineData("Production", "Dev")]
    [InlineData(null, "Dev")]                 // no Environment = Production
    public void A_package_beside_the_exe_of_the_other_environment_refuses_to_start(string? installed, string packaged)
    {
        Write(programData, EnvJson(installed));
        Write(besideExe, EnvJson(packaged, "\"ApiSecret\": \"package-secret\""));
        var before = File.ReadAllText(programData);

        var ex = Assert.Throws<SettingsRefusedException>(Resolve);

        Assert.Equal($"This TillPOS is a {packaged} build but its settings at {programData} are for {installed ?? "Production"}. " +
            "Use the matching build, or remove that settings file.", ex.Message);
        Assert.Equal(before, File.ReadAllText(programData));            // no secret or counters adopted
        Assert.DoesNotContain("package-secret", ex.Message);
    }

    [Fact]
    public void A_built_in_package_of_the_other_environment_refuses_to_start()
    {
        Write(programData, EnvJson("Production", "\"ApiSecretProtected\": \"\""));
        embedded = EnvJson("Dev", "\"ApiSecret\": \"built-in\", \"Counters\": [ { \"PosProfile\": \"Test Counter\", \"CashMode\": \"Cash Counter 2\" } ]");
        var before = File.ReadAllText(programData);

        var ex = Assert.Throws<SettingsRefusedException>(Resolve);

        Assert.StartsWith("This TillPOS is a Dev build but its settings at ", ex.Message);
        Assert.Equal(before, File.ReadAllText(programData));
    }

    [Fact]
    public void Importing_a_package_beside_the_exe_that_disagrees_with_the_built_in_one_refuses()
    {
        Write(besideExe, EnvJson("Production", "\"ApiSecret\": \"x\""));
        embedded = EnvJson("Dev", "\"ApiSecret\": \"y\"");

        Assert.Throws<SettingsRefusedException>(Resolve);
        Assert.False(File.Exists(programData));
    }

    [Theory]
    [InlineData("dev", "Dev")]
    [InlineData("PRODUCTION", "Production")]
    [InlineData(" Dev ", "Dev")]
    public void The_environment_is_read_in_any_case_and_matching_packages_start(string written, string expected)
    {
        Write(programData, EnvJson(written));
        Write(besideExe, EnvJson(expected.ToUpperInvariant()));
        embedded = EnvJson(expected.ToLowerInvariant());

        Assert.Equal(expected, Resolve()!.Environment);
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("")]
    [InlineData("Development")]
    public void An_unknown_environment_refuses_to_start(string environment)
    {
        Write(programData, EnvJson(environment));
        var ex = Assert.Throws<SettingsRefusedException>(Resolve);
        Assert.Contains("\"Dev\" or \"Production\"", ex.Message);

        File.Delete(programData);
        Write(besideExe, EnvJson("Production", "\"ApiSecret\": \"x\""));
        embedded = EnvJson(environment);
        Assert.Throws<SettingsRefusedException>(Resolve);
    }

    [Fact]
    public void A_json_null_environment_is_production()
    {
        Write(programData, EnvJson(null).Replace(" }", ", \"Environment\": null }"));
        Assert.Equal("Production", Resolve()!.Environment);
    }
}
