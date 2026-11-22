using System.Text.Json;
using System.Windows.Markup;

namespace DicomViewer
{
    // Small helpers to read values from DICOM JSON (the format QUIDO-RS returns)
    public static class DicomJson
    { 
        // Returns the first value of a tag as text, or "" if the tag is missing or empty
        public static string GetText(JsonElement item, string tag)
        {
            if (!item.TryGetProperty(tag, out JsonElement element)) return "";
            if (!element.TryGetProperty("Value", out JsonElement values)) return "";
            if (values.GetArrayLength() == 0) return "";

            JsonElement first = values[0];
            return first.ValueKind switch
            {
                JsonValueKind.String => first.GetString() ?? "",
                JsonValueKind.Number => first.ToString(),
                JsonValueKind.Object when first.TryGetProperty("Alphabetic", out JsonElement name) => name.GetString() ?? "", // person names (PN)
                _ => ""
            };
        }

        // Returns all valuse of a tag joined with ", " (for example modalities "CT, PT")
        public static string GetAllText(JsonElement item, string tag)
        {
            if (!item.TryGetProperty(tag, out JsonElement element)) return "";
            if (!element.TryGetProperty("Value", out JsonElement values)) return "";

            var parts = new List<string>();
            foreach (JsonElement value in values.EnumerateArray())
                parts.Add(value.ToString());

            return string.Join(", ", parts);
        }
    }

}
