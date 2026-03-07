---
title: ItemSpawner Mod - In-Game Item Spawner for Terraria 1.4.5
description: Walkthrough of the ItemSpawner mod for Terraria 1.4.5. Full custom UI with search, scrolling, and item catalog. Advanced UI building tutorial.
parent: Walkthroughs
nav_order: 5
---

# ItemSpawner Walkthrough

**Difficulty:** Advanced
**Concepts:** Custom UI, item catalog, search, scrolling, spawning items

ItemSpawner provides a searchable UI to spawn any item in the game (singleplayer only).

## What It Does

Press Insert to open a search window. Type to filter items, then:
- **Left-click** an item to spawn 1 to cursor (Shift+Left: 1 to inventory)
- **Right-click** an item to spawn a full stack to cursor (Shift+Right: full stack to inventory)

Items are sorted alphabetically for easy browsing.

## Key Concepts

### 1. Building an Item Catalog

Cache all items on first use:

```csharp
private static List<ItemInfo> _catalog;
private static bool _catalogBuilt = false;

private static void BuildCatalog()
{
    if (_catalogBuilt) return;

    _catalog = new List<ItemInfo>();

    int itemCount = ItemID.Count; // Total number of items in Terraria 1.4.5
    for (int i = 1; i < itemCount; i++)
    {
        try
        {
            Item item = new Item();
            item.SetDefaults(i);

            if (item.type != 0 && !string.IsNullOrEmpty(item.Name))
            {
                _catalog.Add(new ItemInfo
                {
                    Type = i,
                    Name = item.Name
                });
            }
        }
        catch { }
    }

    // Sort alphabetically
    _catalog.Sort((a, b) => a.Name.CompareTo(b.Name));
    _catalogBuilt = true;

    _log.Info($"Built catalog with {_catalog.Count} items");
}

class ItemInfo
{
    public int Type;
    public string Name;
}
```

### 2. Drawing the UI with Widget Library

The mod uses `DraggablePanel`, `TextInput`, `ScrollView`, and `StackLayout` from the Widget Library:

```csharp
using TerrariaModder.Core.UI.Widgets;

private DraggablePanel _panel;
private TextInput _searchInput;
private ScrollView _scroll;

public void Initialize(ModContext context)
{
    _panel = new DraggablePanel("item-spawner", "Item Spawner", 520, 520);
    _searchInput = new TextInput("Search items...", maxLength: 100);
    _scroll = new ScrollView();

    _panel.RegisterDrawCallback(DrawUI);
    context.RegisterKeybind("toggle", "Toggle", "Open/close", "Insert", () => _panel.Toggle());
}

private void DrawUI()
{
    if (!_panel.BeginDraw()) return;

    // Search input at top
    _searchInput.Update();
    var layout = new StackLayout(_panel.ContentX, _panel.ContentY, _panel.ContentWidth);
    _searchInput.Draw(layout.X, layout.CurrentY, layout.Width);
    layout.Advance(28);

    // Scrollable item list fills remaining space
    DrawItemList(layout.X, layout.CurrentY, layout.Width,
                 _panel.ContentHeight - layout.TotalHeight);

    _panel.EndDraw();
}
```

**Key advantage:** `DraggablePanel` handles input blocking, z-ordering, dragging, close button, and escape-to-close automatically. No manual `RegisterPanelBounds()` or `ConsumeClick()` calls needed.

### 3. Search Filtering

Filter catalog based on search text:

```csharp
private static List<ItemInfo> GetFilteredItems()
{
    if (string.IsNullOrEmpty(_searchText))
        return _catalog;

    string search = _searchText.ToLower();
    return _catalog.Where(item =>
        item.Name.ToLower().Contains(search)
    ).ToList();
}
```

### 4. Scrollable List with ScrollView

Use `ScrollView` for virtual scrolling with culling:

```csharp
private const int ITEM_HEIGHT = 26;

private void DrawItemList(int x, int y, int width, int height)
{
    var items = GetFilteredItems();
    int totalHeight = items.Count * ITEM_HEIGHT;

    _scroll.Begin(x, y, width, height, totalHeight);

    for (int i = 0; i < items.Count; i++)
    {
        int itemY = i * ITEM_HEIGHT;
        if (!_scroll.IsVisible(itemY, ITEM_HEIGHT)) continue;  // Cull off-screen

        int drawY = _scroll.ContentY + itemY;
        var item = items[i];

        bool hover = WidgetInput.IsMouseOver(x, drawY, width, ITEM_HEIGHT);
        if (hover)
            UIRenderer.DrawRect(x, drawY, width, ITEM_HEIGHT, UIColors.ItemHoverBg);

        UIRenderer.DrawItem(item.Type, x + 2, drawY + 2, 22, 22);  // Item icon
        UIRenderer.DrawText(item.Name, x + 28, drawY + 4, UIColors.Text);

        // Left-click: 1 item, Right-click: full stack
        // Shift modifier: place in inventory instead of cursor
        if (hover && WidgetInput.MouseLeftClick)
        {
            bool toInventory = InputState.IsKeyDown(KeyCode.LeftShift);
            if (toInventory) SpawnToInventory(item.Type, 1);
            else SpawnToCursor(item.Type, 1);
            WidgetInput.ConsumeClick();
        }
        else if (hover && WidgetInput.MouseRightClick)
        {
            bool toInventory = InputState.IsKeyDown(KeyCode.LeftShift);
            if (toInventory) SpawnToInventory(item.Type, item.MaxStack);
            else SpawnToCursor(item.Type, item.MaxStack);
            WidgetInput.ConsumeRightClick();
        }
    }

    _scroll.End();  // Draws scrollbar, handles scroll input
}
```

**Key advantage:** `ScrollView` uses virtual scrolling, so only visible items are drawn. This means a 5000+ item catalog renders efficiently. The `IsVisible()` check culls items outside the viewport.

### 5. Text Input with TextInput Widget

The `TextInput` widget handles all keyboard input, IME, focus management, and clear button:

```csharp
private TextInput _searchInput = new TextInput("Search items...", maxLength: 100);

// In Update phase (required for IME support):
_searchInput.Update();

// In Draw phase:
string value = _searchInput.Draw(x, y, width);

// Check if search changed
if (_searchInput.HasChanged)
{
    RebuildFilteredList();
}
```

**Key advantage:** No manual key-to-char conversion, no XNA keyboard state tracking. The widget handles Backspace, Escape-to-unfocus, IME input, clear button (X), and focus border automatically.

### 6. Spawning Items

Two spawn modes: cursor (default) places item on mouse, inventory places in first empty slot:

```csharp
private void SpawnToCursor(int itemType, int stack)
{
    // Place item directly on mouse cursor (like picking up an item)
    var mouseItem = Main.mouseItem;
    mouseItem.SetDefaults(itemType);
    mouseItem.stack = stack;
}

private void SpawnToInventory(int itemType, int stack)
{
    Player player = Main.LocalPlayer;
    player.QuickSpawnItem(/* ... */itemType, stack);
}
```

### 7. Toggle and Lifecycle

With `DraggablePanel`, toggle and cleanup are simpler:

```csharp
public void Initialize(ModContext context)
{
    _panel = new DraggablePanel("item-spawner", "Item Spawner", 520, 520);
    _panel.RegisterDrawCallback(DrawUI);

    context.RegisterKeybind("toggle", "Toggle", "Open spawner", "Insert", () =>
    {
        _panel.Toggle();
        if (_panel.IsOpen) BuildCatalog();
    });
}

public void Unload()
{
    _panel.UnregisterDrawCallback();
}
```

No manual event subscriptions for `OnUIOverlay` or `OnPostUpdate`. `DraggablePanel.RegisterDrawCallback` handles draw registration and z-order management.

## Lessons Learned

1. **Use the Widget Library** - `DraggablePanel` + `TextInput` + `ScrollView` replaces hundreds of lines of manual UI code
2. **Cache expensive operations** - Build item catalog once
3. **Lazy initialization** - Only build when first needed
4. **Virtual scrolling** - `ScrollView.IsVisible()` culls off-screen items for performance
5. **TextInput handles IME** - Don't write manual key-to-char conversion
6. **Cursor vs inventory spawning** - `SpawnToCursor` for quick pickup, `SpawnToInventory` for direct placement
7. **Shift modifier** - Hold Shift to change spawn destination (cursor vs inventory)
8. **Clean up in Unload** - Call `UnregisterDrawCallback()`

For more on the Widget Library, see [Core API Reference - Widget Library](../core-api-reference#widget-library).
For UIRenderer details, see [Core API Reference - UIRenderer](../core-api-reference#uirenderer).
