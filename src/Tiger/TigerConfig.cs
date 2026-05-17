using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tiger;

/// <summary>
/// An AzDO organization/project pair to monitor.
/// </summary>
public sealed class AzdoSource
{
    [JsonPropertyName("organization")]
    public required string Organization { get; set; }

    [JsonPropertyName("project")]
    public required string Project { get; set; }

    [JsonPropertyName("repositories")]
    public List<string> Repositories { get; set; } = [];
}

/// <summary>
/// Root configuration for tiger, loaded from ~/.tiger/config.json.
/// </summary>
public sealed class TigerConfig
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [JsonPropertyName("pollIntervalSeconds")]
    public int PollIntervalSeconds { get; set; } = 300;

    [JsonPropertyName("backfillDays")]
    public int BackfillDays { get; set; } = 7;

    [JsonPropertyName("sources")]
    public List<AzdoSource> Sources { get; set; } = [];

    /// <summary>
    /// Loads config from ~/.tiger/config.json. Returns a default config if the file
    /// doesn't exist.
    /// </summary>
    public static TigerConfig Load(string configDirectory)
    {
        var path = GetConfigPath(configDirectory);
        if (!File.Exists(path))
        {
            return CreateDefault();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<TigerConfig>(json, s_jsonOptions)
            ?? CreateDefault();
    }

    /// <summary>
    /// Saves config to ~/.tiger/config.json.
    /// </summary>
    public void Save(string configDirectory)
    {
        Directory.CreateDirectory(configDirectory);
        var path = GetConfigPath(configDirectory);
        var json = JsonSerializer.Serialize(this, s_jsonOptions);
        File.WriteAllText(path, json);
    }

    public static string GetConfigPath(string configDirectory) =>
        Path.Combine(configDirectory, "config.json");

    private static TigerConfig CreateDefault() => new()
    {
        PollIntervalSeconds = 300,
        Sources =
        [
            new AzdoSource
            {
                Organization = "dnceng-public",
                Project = "public",
                Repositories = ["dotnet/roslyn"],
            }
        ],
        BackfillDays = 3,
    };
}
