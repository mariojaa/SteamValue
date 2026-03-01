using System.Text.Json;

namespace SteamValue.Extensions
{
    /// <summary>
    /// Extension methods for JsonElement to simplify parsing
    /// </summary>
    public static class JsonExtensions
    {
        public static string GetStringOrEmpty(this JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
                ? prop.GetString() ?? ""
                : "";
        }

        public static int GetInt32OrDefault(this JsonElement element, string propertyName, int defaultValue = 0)
        {
            return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number
                ? prop.GetInt32()
                : defaultValue;
        }

        public static long GetInt64OrDefault(this JsonElement element, string propertyName, long defaultValue = 0)
        {
            return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number
                ? prop.GetInt64()
                : defaultValue;
        }

        public static double GetDoubleOrDefault(this JsonElement element, string propertyName, double defaultValue = 0)
        {
            return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number
                ? prop.GetDouble()
                : defaultValue;
        }

        public static bool GetBoolOrDefault(this JsonElement element, string propertyName, bool defaultValue = false)
        {
            if (!element.TryGetProperty(propertyName, out var prop)) return defaultValue;
            
            return prop.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => prop.GetInt32() != 0,
                _ => defaultValue
            };
        }

        public static bool IsSuccess(this JsonElement element)
        {
            if (!element.TryGetProperty("success", out var success))
                return false;

            return success.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.Number => success.GetInt32() == 1,
                _ => false
            };
        }

        public static JsonElement? GetPropertyOrNull(this JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var prop) ? prop : null;
        }

        public static List<JsonElement> EnumerateArraySafe(this JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
                return new List<JsonElement>();

            return prop.EnumerateArray().ToList();
        }
    }

    /// <summary>
    /// Extension methods for IEnumerable
    /// </summary>
    public static class EnumerableExtensions
    {
        public static IEnumerable<T> TakeSafe<T>(this IEnumerable<T> source, int count)
        {
            if (source == null) return Enumerable.Empty<T>();
            return source.Take(Math.Max(0, count));
        }

        public static T? FirstOrDefaultSafe<T>(this IEnumerable<T> source) where T : class
        {
            return source?.FirstOrDefault();
        }
    }
}
