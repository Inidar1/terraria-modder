using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using StorageHub.DedicatedBlocks;
using TerrariaModder.Core.Logging;

namespace StorageHub.Storage
{
    /// <summary>
    /// Persistent disk-backed storage state.
    ///
    /// Disks are identified by (disk item runtime type + disk uid).
    /// The uid is stored on the disk item's prefix field (1..255).
    /// </summary>
    internal sealed class DriveStorageState
    {
        private readonly ILogger _log;
        private readonly string _modFolder;
        private readonly Dictionary<string, DiskRecord> _disks = new Dictionary<string, DiskRecord>(StringComparer.Ordinal);

        private string _currentWorldName;
        private bool _dirty;

        public DriveStorageState(ILogger log, string modFolder)
        {
            _log = log;
            _modFolder = modFolder;
        }

        public IReadOnlyDictionary<string, DiskRecord> Disks => _disks;

        public void Load(string worldName)
        {
            _currentWorldName = SanitizeFileName(worldName);
            _disks.Clear();
            _dirty = false;

            string path = GetDriveDataPath();
            if (!File.Exists(path))
            {
                _log.Debug($"[DriveStorage] No disk data file at {path}");
                return;
            }

            int loadedDisks = 0;
            try
            {
                foreach (string rawLine in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = rawLine?.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#", StringComparison.Ordinal))
                        continue;

                    string[] parts = line.Split('|');
                    if (parts.Length < 1)
                        continue;

                    // Disk header: D|disk_item_type|disk_uid
                    if (string.Equals(parts[0], "D", StringComparison.Ordinal))
                    {
                        if (parts.Length < 3)
                            continue;
                        if (!int.TryParse(parts[1], out int diskItemType))
                            continue;
                        if (!int.TryParse(parts[2], out int diskUid))
                            continue;
                        if (!IsValidDiskIdentity(diskItemType, diskUid))
                            continue;

                        if (TryGetDisk(diskItemType, diskUid, out _))
                            continue;

                        _disks[BuildDiskKey(diskItemType, diskUid)] = new DiskRecord(diskItemType, diskUid);
                        loadedDisks++;
                    }
                    // Disk item stack: I|disk_item_type|disk_uid|item_id|prefix|stack
                    else if (string.Equals(parts[0], "I", StringComparison.Ordinal))
                    {
                        if (parts.Length < 6)
                            continue;
                        if (!int.TryParse(parts[1], out int diskItemType))
                            continue;
                        if (!int.TryParse(parts[2], out int diskUid))
                            continue;
                        if (!int.TryParse(parts[3], out int itemId))
                            continue;
                        if (!int.TryParse(parts[4], out int prefix))
                            prefix = 0;
                        if (!int.TryParse(parts[5], out int stack))
                            continue;

                        if (!IsValidDiskIdentity(diskItemType, diskUid))
                            continue;
                        if (itemId <= 0 || stack <= 0)
                            continue;

                        var disk = EnsureDisk(diskItemType, diskUid);
                        disk.Items.Add(new DriveItemRecord(itemId, prefix, stack));
                    }
                }

                _log.Info($"[DriveStorage] Loaded {_disks.Count} disk record(s) from {path}");
            }
            catch (Exception ex)
            {
                _log.Error($"[DriveStorage] Failed to load {path}: {ex.Message}");
            }
        }

        public void Save()
        {
            if (string.IsNullOrEmpty(_currentWorldName))
                return;

            if (!_dirty)
                return;

            string path = GetDriveDataPath();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                var sb = new StringBuilder();
                sb.AppendLine("# StorageHub disk storage v2");

                foreach (var pair in _disks)
                {
                    DiskRecord disk = pair.Value;
                    sb.Append("D|")
                        .Append(disk.DiskItemType).Append('|')
                        .Append(disk.DiskUid)
                        .AppendLine();

                    for (int i = 0; i < disk.Items.Count; i++)
                    {
                        DriveItemRecord item = disk.Items[i];
                        if (item.ItemId <= 0 || item.Stack <= 0)
                            continue;

                        sb.Append("I|")
                            .Append(disk.DiskItemType).Append('|')
                            .Append(disk.DiskUid).Append('|')
                            .Append(item.ItemId).Append('|')
                            .Append(item.Prefix).Append('|')
                            .Append(item.Stack)
                            .AppendLine();
                    }
                }

                SafeWriteFile(path, sb.ToString());
                _dirty = false;
                _log.Info($"[DriveStorage] Saved {_disks.Count} disk record(s) to {path}");
            }
            catch (Exception ex)
            {
                _log.Error($"[DriveStorage] Failed to save {path}: {ex.Message}");
            }
        }

        public bool TryGetDisk(int diskItemType, int diskUid, out DiskRecord disk)
        {
            disk = null;
            if (!IsValidDiskIdentity(diskItemType, diskUid))
                return false;

            return _disks.TryGetValue(BuildDiskKey(diskItemType, diskUid), out disk);
        }

        public DiskRecord EnsureDisk(int diskItemType, int diskUid)
        {
            if (!IsValidDiskIdentity(diskItemType, diskUid))
                return null;

            string key = BuildDiskKey(diskItemType, diskUid);
            if (_disks.TryGetValue(key, out var existing))
                return existing;

            var disk = new DiskRecord(diskItemType, diskUid);
            _disks[key] = disk;
            _dirty = true;
            return disk;
        }

        public int AllocateDiskUid(int diskItemType)
        {
            if (!DedicatedBlocksManager.TryGetDiskTierForItemType(diskItemType, out _))
                return 0;

            for (int uid = 1; uid <= byte.MaxValue; uid++)
            {
                if (_disks.ContainsKey(BuildDiskKey(diskItemType, uid)))
                    continue;

                return uid;
            }

            return 0;
        }

        public void MarkDirty()
        {
            _dirty = true;
        }

        public bool TryUpgradeDiskIdentity(
            int oldDiskItemType,
            int oldDiskUid,
            int newDiskItemType,
            out int resultingDiskUid,
            out string failureReason)
        {
            resultingDiskUid = 0;
            failureReason = null;

            if (!IsValidDiskIdentity(oldDiskItemType, oldDiskUid))
            {
                failureReason = "Invalid source disk identity.";
                return false;
            }

            if (!DedicatedBlocksManager.TryGetDiskTierForItemType(newDiskItemType, out _))
            {
                failureReason = "Invalid target disk type.";
                return false;
            }

            if (oldDiskItemType == newDiskItemType)
            {
                resultingDiskUid = oldDiskUid;
                return true;
            }

            string oldKey = BuildDiskKey(oldDiskItemType, oldDiskUid);
            int targetUid = oldDiskUid;
            string targetKey = BuildDiskKey(newDiskItemType, targetUid);

            if (_disks.ContainsKey(targetKey))
            {
                targetUid = AllocateDiskUid(newDiskItemType);
                if (targetUid <= 0)
                {
                    failureReason = "No free disk IDs for target tier.";
                    return false;
                }

                targetKey = BuildDiskKey(newDiskItemType, targetUid);
            }

            if (_disks.TryGetValue(oldKey, out var oldDisk))
            {
                var migrated = new DiskRecord(newDiskItemType, targetUid);
                for (int i = 0; i < oldDisk.Items.Count; i++)
                {
                    var entry = oldDisk.Items[i];
                    if (entry.ItemId <= 0 || entry.Stack <= 0)
                        continue;
                    migrated.Items.Add(entry);
                }

                _disks[targetKey] = migrated;
                _disks.Remove(oldKey);
                _dirty = true;
            }

            resultingDiskUid = targetUid;
            return true;
        }

        private static bool IsValidDiskIdentity(int diskItemType, int diskUid)
        {
            if (diskUid <= 0 || diskUid > byte.MaxValue)
                return false;

            return DedicatedBlocksManager.TryGetDiskTierForItemType(diskItemType, out _);
        }

        private static string BuildDiskKey(int diskItemType, int diskUid)
        {
            return diskItemType.ToString() + ":" + diskUid.ToString();
        }

        private string GetDriveDataPath()
        {
            return Path.Combine(_modFolder, "worlds", _currentWorldName, "drive-storage.dat");
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "unknown";

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }
            return sb.ToString();
        }

        private static void SafeWriteFile(string filePath, string content)
        {
            string tempPath = filePath + ".tmp";
            string backupPath = filePath + ".bak";

            File.WriteAllText(tempPath, content, Encoding.UTF8);

            if (File.Exists(filePath))
            {
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
                File.Move(filePath, backupPath);
            }

            File.Move(tempPath, filePath);
        }
    }

    internal sealed class DiskRecord
    {
        public int DiskItemType { get; }
        public int DiskUid { get; }
        public List<DriveItemRecord> Items { get; } = new List<DriveItemRecord>();

        public DiskRecord(int diskItemType, int diskUid)
        {
            DiskItemType = diskItemType;
            DiskUid = diskUid;
        }

        public int Capacity
        {
            get
            {
                if (!DedicatedBlocksManager.TryGetDiskTierForItemType(DiskItemType, out int tier))
                    return 0;

                return StorageDiskCatalog.GetCapacity(tier);
            }
        }
    }

    internal readonly struct DriveItemRecord
    {
        public int ItemId { get; }
        public int Prefix { get; }
        public int Stack { get; }

        public DriveItemRecord(int itemId, int prefix, int stack)
        {
            ItemId = itemId;
            Prefix = prefix;
            Stack = stack;
        }
    }
}
