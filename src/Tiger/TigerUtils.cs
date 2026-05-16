using Azure.Identity;

namespace Tiger;

/// <summary>
/// Configuration and credentials for the tiger tools.
/// </summary>
public sealed class TigerContext
{
    /// <summary>
    /// The directory where tiger configuration and data files are stored.
    /// </summary>
    public string ConfigDirectory { get; }

    /// <summary>
    /// Azure credential for AzDO and other Azure services.
    /// </summary>
    public DefaultAzureCredential AzureCredential { get; }

    /// <summary>
    /// Helix bearer token, if a helix.txt file exists in the config directory.
    /// </summary>
    public string? HelixToken { get; }

    /// <summary>
    /// The loaded tiger configuration.
    /// </summary>
    public TigerConfig Config { get; }

    /// <summary>
    /// Path to the SQLite database file.
    /// </summary>
    public string DatabasePath => Path.Combine(ConfigDirectory, "tiger.db");

    private TigerDatabase? _database;

    /// <summary>
    /// Opens (or returns the already-open) SQLite database.
    /// </summary>
    public TigerDatabase GetDatabase()
    {
        _database ??= TigerDatabase.Open(DatabasePath);
        return _database;
    }

    internal TigerContext(string configDirectory, DefaultAzureCredential azureCredential, string? helixToken, TigerConfig config)
    {
        ConfigDirectory = configDirectory;
        AzureCredential = azureCredential;
        HelixToken = helixToken;
        Config = config;
    }
}

public static class TigerUtils
{
    private const string ConfigDirectoryName = ".tiger";

    public static DefaultAzureCredential CreateCredential() =>
        new DefaultAzureCredential(new DefaultAzureCredentialOptions()
        {
            TenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47",
        });

    /// <summary>
    /// Creates a <see cref="TigerContext"/> with the configuration directory,
    /// Azure credential, config, and optional Helix token.
    /// </summary>
    public static TigerContext CreateContext()
    {
        var configDir = GetConfigDirectory();
        var credential = CreateCredential();
        var helixToken = ReadHelixToken(configDir);
        var config = TigerConfig.Load(configDir);
        return new TigerContext(configDir, credential, helixToken, config);
    }

    /// <summary>
    /// Gets the configuration directory, creating it if it doesn't exist.
    /// Located at ~/.tiger.
    /// </summary>
    public static string GetConfigDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configDir = Path.Combine(home, ConfigDirectoryName);
        Directory.CreateDirectory(configDir);
        return configDir;
    }

    private static string? ReadHelixToken(string configDir)
    {
        var helixTokenPath = Path.Combine(configDir, "helix.txt");
        if (!File.Exists(helixTokenPath))
            return null;

        var token = File.ReadAllText(helixTokenPath).Trim();
        return string.IsNullOrEmpty(token) ? null : token;
    }
}
