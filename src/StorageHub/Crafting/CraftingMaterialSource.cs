using System;
using System.Collections.Generic;
using StorageHub.Storage;
using StorageHub.Relay;

namespace StorageHub.Crafting
{
    /// <summary>
    /// Defines which actual storage snapshots crafting may use. Shared by availability,
    /// recursive planning and execution; reads current range and protection settings.
    /// This is eligibility, not reservation or multiplayer authority.
    /// </summary>
    public sealed class CraftingMaterialSource
    {
        private readonly IStorageProvider _storage;
        private readonly RangeCalculator _range;
        private readonly Func<bool> _protectHotbar;

        public CraftingMaterialSource(IStorageProvider storage, RangeCalculator range = null,
            Func<bool> protectHotbar = null)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _range = range;
            _protectHotbar = protectHotbar;
        }

        public bool ProtectHotbar => _protectHotbar?.Invoke() == true;

        public List<ItemSnapshot> Read()
        {
            var items = _storage.GetAllItems();
            if (_range != null)
            {
                _range.UpdatePlayerPosition();
                items = _range.FilterItemsByRange(items);
            }
            if (ProtectHotbar)
                items = items.FindAll(item => !item.IsFromInventory || item.SourceSlot >= 10);
            return items;
        }
    }
}
