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

    internal TigerContext(string configDirectory, DefaultAzureCredential azureCredential, string? helixToken)
    {
        ConfigDirectory = configDirectory;
        AzureCredential = azureCredential;
        HelixToken = helixToken;
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
    /// Azure credential, and optional Helix token read from helix.txt.
    /// </summary>
    public static TigerContext CreateContext()
    {
        var configDir = GetConfigDirectory();
        var credential = CreateCredential();
        var helixToken = ReadHelixToken(configDir);
        return new TigerContext(configDir, credential, helixToken);
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
