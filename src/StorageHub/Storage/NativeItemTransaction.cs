using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Terraria;
using Terraria.GameContent;
using Terraria.GameContent.Achievements;
using TerrariaModder.Core.Logging;

namespace StorageHub.Storage
{
    /// <summary>
    /// Synchronous SP item journal around native pickup/spawn calls. Retains original
    /// objects and locations; rollback does not recreate items from display snapshots.
    /// Pickup notifications are released only after commit. Not a network transaction,
    /// save backup, or rollback of native sound/text effects.
    /// </summary>
    internal sealed class NativeItemTransaction : IDisposable
    {
        private sealed class Identity : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
        private static readonly Dictionary<Type, FieldInfo[]> Fields = new Dictionary<Type, FieldInfo[]>();
        private readonly Dictionary<object, object[]> _objects = new Dictionary<object, object[]>(new Identity());
        private readonly Dictionary<Array, Array> _arrays = new Dictionary<Array, Array>();
        private readonly IList _pending;
        private readonly object[] _pendingBefore;
        [ThreadStatic] private static NativeItemTransaction _current;
        private readonly Player _player;
        private readonly ILogger _log;
        private readonly List<(Item Item, int Count)> _pickups = new List<(Item, int)>();
        private bool _committed;
        internal NativeItemTransaction(Player player, ILogger log)
        {
            if (Main.netMode != 0) throw new InvalidOperationException("Native item journal requires singleplayer");
            if (_current != null) throw new InvalidOperationException("Reentrant independent crafting transaction");
            NativePickupNotifications.EnsurePatched();
            _player = player; _log = log;
            Capture(player.inventory);
            Capture(player.bank.item); Capture(player.bank2.item);
            Capture(player.bank3.item); Capture(player.bank4.item);
            Capture(Main.item); Capture(Main.timeItemSlotCannotBeReusedFor);
            // Native overflow owns these private mutable structures; public Item APIs
            // provide no journal/restore operation. Verify them before any item mutation.
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            var cache = typeof(Item).GetField("cachedItemSpawnsByType", flags)?.GetValue(null) as Array;
            if (cache == null) throw new MissingFieldException("Item.cachedItemSpawnsByType");
            Capture(cache);
            _pending = typeof(EmergencyStacking).GetField("PendingTransfers", flags)?.GetValue(null) as IList;
            if (_pending == null) throw new MissingFieldException("EmergencyStacking.PendingTransfers");
            _pendingBefore = new object[_pending.Count]; _pending.CopyTo(_pendingBefore, 0);
            _current = this;
        }
        internal void Capture(Array array)
        {
            if (array == null || _arrays.ContainsKey(array)) return;
            _arrays.Add(array, (Array)array.Clone());
            foreach (var value in array)
            {
                if (value is Item) CaptureObject(value);
                else if (value is WorldItem world)
                { CaptureObject(world); CaptureObject(world.inner); }
            }
        }
        private static FieldInfo[] GetFields(Type type)
        {
            lock (Fields)
            {
                if (Fields.TryGetValue(type, out var fields)) return fields;
                var result = new List<FieldInfo>();
                for (var owner = type; owner != null && owner != typeof(object); owner = owner.BaseType)
                    result.AddRange(owner.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                fields = result.ToArray(); Fields.Add(type, fields); return fields;
            }
        }
        private void CaptureObject(object value)
        {
            if (value == null || _objects.ContainsKey(value)) return;
            var fields = GetFields(value.GetType());
            var values = new object[fields.Length];
            for (int i = 0; i < fields.Length; i++) values[i] = fields[i].GetValue(value);
            _objects.Add(value, values);
        }
        internal static bool QueuePickup(Player player, Item item, int count)
        {
            var transaction = _current;
            if (transaction == null || transaction._committed || !ReferenceEquals(transaction._player, player)) return false;
            transaction._pickups.Add((item.Clone(), count));
            return true;
        }
        internal void Commit()
        {
            _committed = true;
            _current = null;
            foreach (var pickup in _pickups)
            {
                try { AchievementsHelper.NotifyItemPickup(_player, pickup.Item, pickup.Count); }
                catch (Exception ex)
                {
                    // Items are committed. A subscriber failure cannot truthfully turn
                    // this into a rejected craft or cause an item rollback.
                    _log.Error($"Craft committed but pickup notification failed: {ex}");
                }
            }
            _pickups.Clear();
        }
        public void Dispose()
        {
            if (_committed) return;
            if (ReferenceEquals(_current, this)) _current = null;
            _pickups.Clear();
            foreach (var entry in _objects)
            {
                var fields = GetFields(entry.Key.GetType());
                for (int i = 0; i < fields.Length; i++)
                    if (!Equals(fields[i].GetValue(entry.Key), entry.Value[i])) fields[i].SetValue(entry.Key, entry.Value[i]);
            }
            foreach (var entry in _arrays) Array.Copy(entry.Value, entry.Key, entry.Value.Length);
            _pending.Clear(); foreach (var value in _pendingBefore) _pending.Add(value);
            _committed = true;
        }
    }
}
