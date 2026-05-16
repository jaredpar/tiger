using System.Text.Json;

namespace Tiger;

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
