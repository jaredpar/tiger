using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tiger;

public static class AzdoRepositoryTypes
{
    public const string GitHub = "GitHub";
    public const string TfsGit = "TfsGit";

    public static bool IsGitHub(string? repositoryType) =>
        repositoryType is null || repositoryType.Equals(GitHub, StringComparison.OrdinalIgnoreCase);

    public static bool IsSupported(string? repositoryType) =>
        repositoryType is not null &&
        (repositoryType.Equals(GitHub, StringComparison.OrdinalIgnoreCase) ||
         repositoryType.Equals(TfsGit, StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string? repositoryType)
    {
        if (repositoryType is null)
        {
            return GitHub;
        }

        if (repositoryType.Equals(GitHub, StringComparison.OrdinalIgnoreCase))
        {
            return GitHub;
        }

        if (repositoryType.Equals(TfsGit, StringComparison.OrdinalIgnoreCase))
        {
            return TfsGit;
        }

        throw new InvalidOperationException($"Unsupported AzDO repository type '{repositoryType}'. Supported values are '{GitHub}' and '{TfsGit}'.");
    }
}

/// <summary>
/// An AzDO organization/project pair to monitor.
/// </summary>
public sealed class AzdoSource
{
    [JsonPropertyName("organization")]
    public required string Organization { get; set; }

    [JsonPropertyName("project")]
    public required string Project { get; set; }

    [JsonPropertyName("repositoryType")]
    public string RepositoryType { get; set; } = AzdoRepositoryTypes.GitHub;

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
        var config = JsonSerializer.Deserialize<TigerConfig>(json, s_jsonOptions)
            ?? CreateDefault();
        config.Normalize();
        return config;
    }

    /// <summary>
    /// Saves config to ~/.tiger/config.json.
    /// </summary>
    public void Save(string configDirectory)
    {
        Normalize();
        Directory.CreateDirectory(configDirectory);
        var path = GetConfigPath(configDirectory);
        var json = JsonSerializer.Serialize(this, s_jsonOptions);
        File.WriteAllText(path, json);
    }

    public static string GetConfigPath(string configDirectory) =>
        Path.Combine(configDirectory, "config.json");

    private void Normalize()
    {
        foreach (var source in Sources)
        {
            source.RepositoryType = AzdoRepositoryTypes.Normalize(source.RepositoryType);
        }
    }

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
