using System.Text.Json.Serialization;

namespace TerrariaModManager.Models;

public class NexusModFile
{
    [JsonPropertyName("file_id")]
    public int FileId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("category_name")]
    public string CategoryName { get; set; } = "";

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("size_kb")]
    public int SizeKb { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("is_primary")]
    public bool IsPrimary { get; set; }

    [JsonPropertyName("uploaded_timestamp")]
    public long UploadedTimestamp { get; set; }
}

public class NexusModFiles
{
    [JsonPropertyName("files")]
    public List<NexusModFile> Files { get; set; } = new();
}

public class NexusDownloadLink
{
    [JsonPropertyName("URI")]
    public string Uri { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("short_name")]
    public string ShortName { get; set; } = "";
}
