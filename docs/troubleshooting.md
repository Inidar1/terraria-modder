---
title: Troubleshooting TerrariaModder - Fix Common Issues
description: Solutions for common TerrariaModder problems on Terraria 1.4.5.8. Fix launch issues, mod loading errors, crashes, keybind conflicts, and more.
nav_order: 3
---

# Troubleshooting

Common issues and solutions for TerrariaModder.

## Installation Issues

### "TerrariaInjector.exe doesn't launch the game"

**Symptoms:** Double-clicking TerrariaInjector.exe does nothing or shows an error.

**Solutions:**
1. Verify Terraria.exe exists in the same folder
2. Run as Administrator
3. Check antivirus isn't blocking it
4. Try launching from command prompt to see errors:
   ```cmd
   cd "C:\Program Files (x86)\Steam\steamapps\common\Terraria"
   TerrariaInjector.exe
   ```

### "Game launches but mods don't work"

**Symptoms:** Game runs but no mod features appear.

**Solutions:**
1. Verify folder structure:
   ```
   Terraria/
   ├── TerrariaInjector.exe
   ├── Terraria.exe
   └── TerrariaModder/
       ├── core/
       │   ├── TerrariaModder.Core.dll
       │   └── deps/
       │       └── 0Harmony.dll
       └── mods/
           └── [mod folders]
   ```
2. Check logs at `TerrariaModder/core/logs/`
3. Verify you launched via TerrariaInjector.exe (not Terraria.exe directly)

### "Missing 0Harmony.dll"

**Symptoms:** Error about missing Harmony library.

**Solutions:**
1. Ensure `TerrariaModder/core/deps/0Harmony.dll` exists
2. Re-download TerrariaModder and extract all files

### "Load from network location" or "sandboxed" error

**Symptoms:** Error mentioning "network location", "sandboxed", or "loadFromRemoteSources".

**Cause:** Windows blocks DLLs downloaded from the internet.

**Solutions:**
1. TerrariaInjector automatically unblocks DLLs, so this error is rare
2. If it still occurs, right-click the ZIP file → Properties → check "Unblock" before extracting
3. Or manually unblock each DLL file via Properties → Unblock

## Mod Loading Issues

### "Mod not appearing"

**Symptoms:** Mod folder exists but mod doesn't load.

**Check:**
1. manifest.json exists and is valid JSON
2. DLL file exists in the mod folder
3. If mod has a config, check `Enabled` is `true` in `core/configs/{mod-id}.client.json`
4. No JSON syntax errors (missing commas, brackets)

**Validate JSON:**
```powershell
Get-Content manifest.json | ConvertFrom-Json
```

### "Core version mismatch"

**Symptoms:** Log shows version warning for mod.

**Meaning:** Mod requires a newer version of TerrariaModder Core.

**Solutions:**
1. Update TerrariaModder to latest version
2. Or use an older version of the mod

### "Dependency not found"

**Symptoms:** Mod fails to load, mentions missing dependency.

**Solutions:**
1. Install the required dependency mod
2. Check mod documentation for requirements

## Runtime Issues

### "Feature X doesn't work"

**Symptoms:** Mod loads but specific feature isn't working.

**Debug steps:**
1. Check logs for errors
2. Verify keybind isn't conflicting (check ModMenu F6)
3. Ensure you're in correct game state (in-world, singleplayer, etc.)
4. Try with only that mod enabled

### "Keybind not responding"

**Solutions:**
1. Open ModMenu (F6) and verify keybind is set
2. Check for conflicts with other mods or game keys
3. Try a different key
4. Some keys don't work in certain contexts (menu vs in-game)
5. Note: `MouseLeft` cannot be used for keybinds (reserved for UI interaction)

### "NumPad keys conflict with QuickKeys"

**Meaning:** QuickKeys uses NumPad1-9 and NumPad0 for extended hotbar (slots 11-20). These are registered as normal keybinds and can be rebound via ModMenu (F6).

**Solutions:**
1. Open ModMenu (F6) and go to QuickKeys
2. Rebind the conflicting NumPad keys to different keys
3. Or rebind your other mod's keybinds to avoid the conflict

### "Game Restart Required" shown in mod menu

**Meaning:** The mod doesn't support hot reload. Config changes are saved to disk immediately, but will only take effect after restarting the game.

**Why this happens:** The mod doesn't implement `OnConfigChanged()` method, so it can't receive live config updates.

**Solutions:**
1. Restart the game after changing settings
2. For mod authors: implement `OnConfigChanged()` to support hot reload

### How to implement OnConfigChanged (for mod authors)

Add this method to your mod class to support hot reload:

```csharp
public class Mod : IMod
{
    private bool _enabled;
    private int _maxItems;
    private ModContext _context;

    public void Initialize(ModContext context)
    {
        _context = context;
        LoadConfig();
    }

    // Called by mod menu automatically when a config value changes
    public void OnConfigChanged()
    {
        LoadConfig();
        _context.Logger.Info("Config reloaded!");
    }

    private void LoadConfig()
    {
        var config = _context.Config;
        _enabled = config.Get<bool>("enabled");
        _maxItems = config.Get<int>("maxItems");
    }
}
```

**Important:** Without `OnConfigChanged()`, the mod menu shows "Game Restart Required" badge next to your mod. All config changes are still saved to disk immediately, but they won't be applied until restart.

### Manually editing config files

Config files are stored in the centralized configs folder:
```
Terraria/TerrariaModder/core/configs/{mod-id}.client.json    (client settings)
Terraria/TerrariaModder/core/configs/{mod-id}.server.json    (server settings, admin-controlled)
```

For example:
- `Terraria/TerrariaModder/core/configs/quick-keys.client.json`
- `Terraria/TerrariaModder/core/configs/storage-hub.client.json`

Legacy config files (`mods/{mod-id}/config.json`) are automatically migrated on first load.

Keybind overrides are stored at:
```
Terraria/TerrariaModder/core/keybinds.json
```

You can edit these files directly when the game is closed. Changes will take effect on next launch.

### Keybind conflicts

**Symptoms:** Multiple mods trying to use the same key.

**Solutions:**
1. Open ModMenu (F6)
2. Look for warning icons next to keybinds
3. Rebind one of the conflicting keys
4. Your keybind changes are saved to `TerrariaModder/core/keybinds.json` and persist across restarts

### Number field editing in mod menu

For int/float config fields:
- **Click +/- buttons**: Increment/decrement by step value
- **Hold +/- buttons**: 400ms initial delay, then 50ms repeat rate
- **Click the number**: Type a value directly
  - First keystroke replaces the old value
  - Subsequent keystrokes append
  - Press Enter or click elsewhere to confirm

### "Game crashes when using mod feature"

**Debug steps:**
1. Check `TerrariaModder/core/logs/` for stack trace
2. Try disabling other mods to isolate issue
3. Report to mod author with:
   - Error message from log
   - What you were doing when it crashed
   - Mod versions installed

### "UI elements not appearing"

**For mods with custom UI:**
1. Verify trigger key is pressed
2. Check if UI requires inventory open
3. Some UIs only work in singleplayer
4. Check logs for rendering errors

### Scroll wheel or keys activating game actions during modal UI

If scroll wheel changes hotbar or keys trigger actions while your UI is open:

**Recommended:** Use `DraggablePanel` from the Widget Library. It handles all input blocking automatically.

**Manual approach:**
1. Use panel registration: `UIRenderer.RegisterPanelBounds(panelId, x, y, width, height)`
2. Call `UIRenderer.ConsumeScroll()` after reading scroll value
3. Set `UIRenderer.IsWaitingForKeyInput = true` when capturing keys
4. Call `UIRenderer.UnregisterPanelBounds(panelId)` when closing UI

### Clicks going through my custom UI to inventory

**Symptoms:** Clicking on your custom UI panel also clicks on inventory slots behind it.

**Solutions:**
1. **Best:** Use `DraggablePanel` from the Widget Library, which handles click-through prevention automatically
2. **Manual:** Use `UIRenderer.RegisterPanelBounds("my-panel-id", x, y, width, height)` every frame while visible
3. Call `UnregisterPanelBounds("my-panel-id")` when closing your UI
4. The framework automatically blocks clicks via:
   - `ItemSlot.Handle` patch (inventory slots)
   - `PlayerInput.IgnoreMouseInterface` patch (HUD buttons like Quick Stack, Bestiary, Sort)

### Multiple UI panels not working correctly

**Symptoms:** When multiple mod panels are open, clicks go to the wrong one.

**Solutions:**
1. **Best:** Use `DraggablePanel`, which handles z-order and click-to-focus automatically
2. **Manual:** Use `UIRenderer.ShouldBlockForHigherPriorityPanel("my-panel-id")` before handling clicks
3. Panels use dynamic z-order with click-to-focus: clicking a panel brings it to the front. ModMenu always stays on top when open.

## Performance Issues

### "Game runs slower with mods"

**Solutions:**
1. Disable mods you don't use
2. Check which mod is causing slowdown:
   - Disable all, enable one at a time
3. Some mods are heavier (tile scanning, complex UI)

### "Lag spikes every few seconds"

**Possible causes:**
- Mod doing expensive operations (tile scanning)
- Log file getting very large
- Mod not throttling updates properly

**Solutions:**
1. Check if specific mod causes it
2. Clear old log files
3. Report to mod author

## Log Files

### Where are logs?

```
Terraria/TerrariaModder/core/logs/
```

### Understanding log entries

```
[2024-01-15 10:30:45] [INFO ] [mod-id] Message here
```

- Timestamp: When it happened
- Level: INFO, WARN, ERROR, DEBUG (padded to 5 chars)
- Mod ID: Which mod logged it (in brackets)
- Message: The actual log content

### Common log messages

**Good messages:**
```
[INFO] [core] === Loaded 5 mod(s), 0 dependency error(s), 0 load error(s) ===
[INFO] [skip-intro] Intro skipped!
[INFO] [quick-keys] Registered keybind: auto-torch -> Tilde
```

**Warning messages:**
```
[WARN] [mod-id] Could not find method X
```
Usually means reflection failed - feature may not work.

**Error messages:**
```
[ERROR] [mod-id] Exception in feature: NullReferenceException
```
Something broke - report to mod author with full stack trace.

## Development Issues

### "Build fails"

**Common causes:**
1. Wrong .NET Framework version (need 4.8)
2. Missing references (Terraria.exe, Harmony, Core)
3. Syntax errors in code

### "Mod builds but doesn't load"

**Check:**
1. DLL was copied to mods folder
2. DLL name matches what's expected
3. manifest.json id matches expectations

### "Patches not applying"

**Debug:**
1. **Manual patches:** Apply them in `IModLifecycle.OnContentReady()` and log that the method ran
2. Check that the target method exists and matches the current signature
3. Verify the correct `BindingFlags`
4. Use a unique Harmony ID

### "Reflection returns null"

**Solutions:**
1. Try different BindingFlags combinations
2. Field/method name might differ in this Terraria version
3. Use ILSpy/dnSpy to verify actual names
4. Try both `GetField` and `GetProperty`

## Getting Help

### What to include when asking for help

1. **What you expected** to happen
2. **What actually happened**
3. **Steps to reproduce**
4. **Relevant log output** (not the entire file)
5. **Mod versions** you're using
6. **Terraria version**

### Where to ask

1. GitHub Issues (for specific mod's repository)
2. Terraria modding Discord servers
3. Terraria Community Forums

## Quick Fixes Checklist

- [ ] Launched via TerrariaInjector.exe?
- [ ] Folder structure correct?
- [ ] manifest.json valid JSON?
- [ ] Check mod's config.json for enabled: true (if applicable)?
- [ ] Checked the newest `terrariamodder.client.session-*.log` or server session log for errors?
- [ ] Tried disabling other mods?
- [ ] Tried fresh config (delete config.json)?
- [ ] Correct Terraria version (1.4.5.8)?
