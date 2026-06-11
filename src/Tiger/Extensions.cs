
using System.Text.Json;

namespace Tiger;

public static class Extensions
{
    extension (JsonElement element)
    {
        public string? GetStringProperty(string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) ? value.GetString() : null;
        }

        public int GetInt32Property(string propertyName, int defaultValue = 0)
        {
            return element.TryGetProperty(propertyName, out var value) ? value.GetInt32() : defaultValue;
        }
    }
}