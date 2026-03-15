using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using TerrariaModder.Core.Manifest;

namespace TerrariaModder.Core.Nexus
{
    internal sealed class NexusUser
    {
        [JsonPropertyName("user_id")]
        public int UserId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("is_premium")]
        public bool IsPremium { get; set; }

        [JsonPropertyName("is_supporter")]
        public bool IsSupporter { get; set; }

        [JsonPropertyName("profile_url")]
        public string ProfileUrl { get; set; }
    }

    internal sealed class NexusMod
    {
        [JsonPropertyName("mod_id")]
        public int ModId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("summary")]
        public string Summary { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; }

        [JsonPropertyName("author")]
        public string Author { get; set; }

        [JsonPropertyName("picture_url")]
        public string PictureUrl { get; set; }

        [JsonPropertyName("mod_downloads")]
        public int Downloads { get; set; }

        [JsonPropertyName("mod_unique_downloads")]
        public int UniqueDownloads { get; set; }

        [JsonPropertyName("endorsement_count")]
        public int EndorsementCount { get; set; }

        [JsonPropertyName("updated_timestamp")]
        public long UpdatedTimestamp { get; set; }

        [JsonPropertyName("created_timestamp")]
        public long CreatedTimestamp { get; set; }

        [JsonPropertyName("available")]
        public bool Available { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }

        public bool IsInstalled { get; set; }
        public string InstalledVersion { get; set; }
        public bool HasNewerVersion { get; set; }
        public int InstalledFileId { get; set; }
        public bool IsPendingDelete { get; set; }
        public bool PendingDeleteIncludesSettings { get; set; }
    }

    internal sealed class NexusModFile
    {
        [JsonPropertyName("file_id")]
        public int FileId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; }

        [JsonPropertyName("category_name")]
        public string CategoryName { get; set; }

        [JsonPropertyName("file_name")]
        public string FileName { get; set; }

        [JsonPropertyName("size_kb")]
        public int SizeKb { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("is_primary")]
        public bool IsPrimary { get; set; }

        [JsonPropertyName("uploaded_timestamp")]
        public long UploadedTimestamp { get; set; }
    }

    internal sealed class NexusModFiles
    {
        [JsonPropertyName("files")]
        public List<NexusModFile> Files { get; set; } = new List<NexusModFile>();
    }

    internal sealed class NexusDownloadLink
    {
        [JsonPropertyName("URI")]
        public string Uri { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("short_name")]
        public string ShortName { get; set; }
    }

    internal sealed class UpdatedModEntry
    {
        [JsonPropertyName("mod_id")]
        public int ModId { get; set; }

        [JsonPropertyName("latest_file_update")]
        public long LatestFileUpdate { get; set; }

        [JsonPropertyName("latest_mod_activity")]
        public long LatestModActivity { get; set; }
    }

    internal sealed class InstalledModRecord
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Author { get; set; }
        public string Description { get; set; }
        public string EntryDll { get; set; }
        public string FolderPath { get; set; }
        public bool IsEnabled { get; set; }
        public bool IsCore { get; set; }
        public bool HasConfigFiles { get; set; }
        public ModManifest Manifest { get; set; }
        public int NexusModId { get; set; }
        public bool HasUpdate { get; set; }
        public string LatestVersion { get; set; }
        public int LatestFileId { get; set; }
        public bool IsPendingDelete { get; set; }
        public bool PendingDeleteIncludesSettings { get; set; }
    }

    internal sealed class InstallResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public string InstalledModId { get; set; }
        public string DownloadedFilePath { get; set; }
    }

    internal sealed class NexusAuthState
    {
        public string ApiKey { get; set; }
        public int UserId { get; set; }
        public string UserName { get; set; }
        public bool IsPremium { get; set; }
        public bool IsSupporter { get; set; }
        public string ProfileUrl { get; set; }
        public DateTime LastValidatedUtc { get; set; }
    }

    internal sealed class InstallDecisionContext
    {
        public bool IsInstalled { get; set; }
        public bool HasConfigFiles { get; set; }
        public bool HasUpdate { get; set; }
        public string InstalledVersion { get; set; }
        public string LatestVersion { get; set; }
        public NexusModFile MainFile { get; set; }
    }

    internal enum ConfigPreservationMode
    {
        Keep,
        Delete
    }
}
