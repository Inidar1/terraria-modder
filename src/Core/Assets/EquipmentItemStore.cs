using System;
using System.Collections.Generic;
using System.IO;
using Terraria;

namespace TerrariaModder.Core.Assets
{
    /// <summary>Stable item records and independently retained unresolved equipment.</summary>
    public sealed class EquipmentItemStore
    {
        private sealed class Slot { internal Func<Item> Get; internal Action<Item> Set; }
        private readonly Dictionary<string, Slot> _slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
        private readonly List<object> _unresolved = new List<object>();
        public int UnresolvedCount => _unresolved.Count;
        public List<object> SnapshotUnresolved() => new List<object>(_unresolved);

        public void ReadSlot(string location, object record, Func<Item> get, Action<Item> set)
        {
            _slots[location] = new Slot { Get = get, Set = set };
            if (TryReadItem(record, out var item)) set(item);
            else _unresolved.Add(new Dictionary<string, object> { ["location"] = location, ["item"] = record });
        }

        public void ReadUnresolved(object records)
        {
            if (records == null) return;
            if (!(records is List<object> list)) { _unresolved.Add(records); return; }
            foreach (object record in list)
            {
                if (record is Dictionary<string, object> data && data.TryGetValue("location", out var location) && location is string key
                    && data.TryGetValue("item", out var raw) && _slots.TryGetValue(key, out var slot)
                    && (slot.Get() == null || slot.Get().IsAir) && TryReadItem(raw, out var item))
                    slot.Set(item);
                else _unresolved.Add(record);
            }
        }

        /// <summary>Read the supported equipment envelope before exposing any of its slots.</summary>
        public static Dictionary<string, object> ReadDocument(string json, bool extraSlots)
        {
            var data = TerrariaModder.Core.IO.SidecarJson.Deserialize(json);
            if (data.TryGetValue("version", out var version) && (!(version is int v) || v != 1))
                throw new InvalidDataException("Unsupported equipment sidecar version");
            if (data.TryGetValue("activeLoadoutIndex", out var active) && (!(active is int index) || index < 0 || index >= 3))
                throw new InvalidDataException("Invalid equipment loadout index");
            if (!data.ContainsKey(extraSlots ? "extraSlots" : "functional"))
                throw new InvalidDataException("Equipment sidecar is missing its active equipment row");
            ValidateRow(data, extraSlots);
            if (data.TryGetValue("loadouts", out var raw))
            {
                if (!(raw is List<object> rows) || rows.Count > 3) throw new InvalidDataException("Invalid equipment loadout rows");
                foreach (object row in rows)
                {
                    if (!(row is Dictionary<string, object> map)) throw new InvalidDataException("Invalid equipment loadout row");
                    ValidateRow(map, extraSlots);
                }
            }
            return data;
        }
        private static void ValidateRow(Dictionary<string, object> row, bool extraSlots)
        {
            if (extraSlots && row.TryGetValue("extraSlots", out var raw))
            {
                if (!(raw is List<object> slots) || slots.Count > 1024) throw new InvalidDataException("Invalid equipment slot list");
                foreach (object slot in slots)
                    if (!(slot is Dictionary<string, object>)) throw new InvalidDataException("Invalid equipment slot row");
            }
        }

        public static Dictionary<string, object> WriteItem(Item item)
        {
            if (item == null || item.IsAir) return new Dictionary<string, object> { ["type"] = 0, ["stack"] = 0, ["prefix"] = 0 };
            string id = ItemRegistry.GetPersistentId(item.type);
            if (id == null) throw new IOException("Cannot persist equipment with unregistered runtime type " + item.type);
            return new Dictionary<string, object> { ["type"] = item.type, ["itemId"] = id,
                ["stack"] = item.stack, ["prefix"] = (int)item.prefix, ["favorited"] = item.favorited };
        }

        private static bool TryReadItem(object record, out Item item)
        {
            item = new Item();
            if (record == null) return true;
            if (!(record is Dictionary<string, object> data)) return false;
            // Do not discard fields from an unfamiliar item schema when decoding
            // a seemingly usable identity. Keep the complete record for recovery.
            foreach (string key in data.Keys)
                if (key != "type" && key != "itemId" && key != "stack" && key != "prefix" && key != "favorited") return false;
            int type;
            if (data.TryGetValue("itemId", out var identity))
            {
                if (!(identity is string id)) return false;
                type = ItemRegistry.ResolvePersistentId(id);
                if (type <= 0) return false; // Never fall back to a stale runtime number.
            }
            else
            {
                if (!data.ContainsKey("type") || !ReadInt(data, "type", 0, out type)) return false;
                if (type == 0)
                    return ReadInt(data, "stack", 0, out int emptyStack) && emptyStack == 0
                        && ReadInt(data, "prefix", 0, out int emptyPrefix) && emptyPrefix == 0
                        && (!data.TryGetValue("favorited", out var emptyFavorite) || emptyFavorite is bool flag && !flag);
                // Legacy native item numbers retain compatibility. A legacy custom
                // number lacks identity: preserve it without guessing its occupant.
                if (type < 0 || type >= ItemRegistry.VanillaItemCount) return false;
            }
            if (!ReadInt(data, "stack", 1, out int stack) || stack <= 0
                || !ReadInt(data, "prefix", 0, out int prefix) || prefix < 0 || prefix > byte.MaxValue) return false;
            if (data.TryGetValue("favorited", out var favorite) && !(favorite is bool)) return false;
            try
            {
                item.SetDefaults(type); item.stack = stack; item.prefix = (byte)prefix;
                item.favorited = favorite is bool value && value;
                return true;
            }
            catch { item = new Item(); return false; }
        }
        private static bool ReadInt(Dictionary<string, object> data, string key, int fallback, out int value)
        {
            value = fallback;
            if (!data.TryGetValue(key, out var raw)) return true;
            if (raw is int number) { value = number; return true; }
            if (raw is long big && big >= int.MinValue && big <= int.MaxValue) { value = (int)big; return true; }
            return false;
        }
    }
}
