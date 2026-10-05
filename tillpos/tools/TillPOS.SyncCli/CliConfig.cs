using System.Text.Json;
using TillPOS.Core.Money;

namespace TillPOS.SyncCli;

public sealed record CliConfig(
    string BaseUrl,
    string ApiKey,
    string ApiSecret,
    string PosProfile,
    string DbPath = "tillpos-cli.db",
    int Precision = 2,
    RoundingMethod Rounding = RoundingMethod.Bankers,
    bool AllowWrites = false)
{
    public static CliConfig Load(string path) =>
        JsonSerializer.Deserialize<CliConfig>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })
        ?? throw new InvalidOperationException($"Could not read {path}.");
}
