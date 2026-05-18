using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tiger;

/// <summary>
/// Ensures the tiger skills directory is registered in the Copilot CLI settings
/// so that skills are available to any copilot session.
/// </summary>
public static class SkillsRegistration
{
    private static readonly string s_settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".copilot", "settings.json");

    /// <summary>
    /// Ensures the skills directory next to the running application is listed
    /// in ~/.copilot/settings.json under "skillDirectories".
    /// </summary>
    public static void EnsureSkillsRegistered()
    {
        var skillsDir = GetSkillsDirectory();
        if (skillsDir is null || !Directory.Exists(skillsDir))
            return;

        try
        {
            var settingsDir = Path.GetDirectoryName(s_settingsPath)!;
            Directory.CreateDirectory(settingsDir);

            JsonNode? root;
            if (File.Exists(s_settingsPath))
            {
                var json = File.ReadAllText(s_settingsPath);
                root = JsonNode.Parse(json) ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            var obj = root.AsObject();

            if (obj.TryGetPropertyValue("skillDirectories", out var existing) && existing is JsonArray array)
            {
                // Check if already present
                var alreadyPresent = array.Any(item =>
                    item is not null &&
                    string.Equals(item.GetValue<string>(), skillsDir, StringComparison.OrdinalIgnoreCase));

                if (!alreadyPresent)
                {
                    array.Add(skillsDir);
                }
                else
                {
                    return; // already registered
                }
            }
            else
            {
                obj["skillDirectories"] = new JsonArray(skillsDir);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(s_settingsPath, root.ToJsonString(options));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to register skills directory: {ex.Message}");
            Console.Error.WriteLine($"  Settings path: {s_settingsPath}");
            Console.Error.WriteLine($"  Skills dir: {skillsDir}");
        }
    }

    private static string? GetSkillsDirectory()
    {
        // Skills directory is next to the running executable
        var appDir = AppContext.BaseDirectory;
        var skillsDir = Path.Combine(appDir, "skills");
        if (!Directory.Exists(skillsDir))
        {
            Console.Error.WriteLine($"Warning: Skills directory not found at {skillsDir}");
            return null;
        }
        return skillsDir;
    }
}
