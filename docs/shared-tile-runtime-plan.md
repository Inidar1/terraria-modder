# Shared Tile Runtime Plan

## Goal

Move experimental custom tile support out of `TerrariaModder.Core` into a shared runtime/library that can be distributed alongside tile mods, while still supporting multiple mods in the same Terraria process.

This is not a straight "extract classes to another DLL" change. The current tile system owns global runtime state:

- one shared custom tile registry
- one shared runtime tile ID space
- one global array-extension pass over Terraria internals
- one global save/load interception path
- one global texture injection and tile behavior patch set

Any replacement has to preserve those properties.

## Current Constraints

## 1. Tile support is process-global

Today the implementation mutates global Terraria state:

- `TileID.Count`
- `TileID.Sets.*`
- `Main.tile*` arrays
- `Lang` tile arrays
- `TextureAssets.Tile`
- other assembly-wide tile-sized arrays
- `SceneMetrics` instances

That means multiple copies of the runtime cannot safely behave independently.

## 2. Registration must happen before runtime IDs are assigned

`TileRegistry` currently rejects late registration after type assignment. The shared runtime has to keep the same model:

- mods register tile definitions during mod initialization
- runtime freezes registrations
- runtime assigns deterministic IDs once
- runtime applies global patches once

## 3. Patch timing matters

The framework applies deferred patches in `OnGameReady`, not during mod initialization. That timing is important because:

- manual Harmony patching is expected there
- `LoadContent()` happens before `OnGameReady`
- texture injection needs both content availability and patch state

Any library approach needs a bootstrap that still participates in those lifecycle hooks.

## 4. The framework does not currently provide shared assembly loading

The loader supports mod dependency ordering, but each mod assembly is loaded directly from its folder and the codebase does not currently show a dedicated shared-library resolution path for mod dependencies.

This means a multi-mod tile runtime needs one of these:

- a core-level assembly resolver for shared libraries
- a designated "runtime mod" that other tile mods depend on
- a bootstrap convention that ensures only one assembly copy is loaded

Without that, each mod shipping its own copy of the runtime creates split static state.

## Recommended Architecture

Use a two-layer design:

## Layer 1: Shared runtime assembly

Suggested name: `TerrariaModder.TileRuntime`

Responsibilities:

- global tile registry
- tile definition model
- runtime tile ID assignment
- array extension
- texture injection
- tile metadata registration
- tile behavior patches
- tile save/load sidecar handling
- diagnostics and compatibility checks

This assembly should be the single owner of all tile-global state.

## Layer 2: Thin bootstrap assembly

Suggested name: `TerrariaModder.TileRuntime.Bootstrap` or a runtime mod assembly

Responsibilities:

- expose lifecycle hooks discovered by the injector
- initialize the shared runtime
- call `TileRuntime.OnGameReady()`
- call `TileRuntime.OnContentLoaded()`
- call `TileRuntime.OnUpdate()`
- optionally expose a minimal mod/API surface for dependent mods

Reason: the injector discovers static lifecycle methods on mod assemblies, not arbitrary referenced libraries.

## Distribution Models

## Option A: Runtime mod

Package the shared runtime as its own mod with a manifest, for example:

- mod ID: `tile-runtime`
- DLL: bootstrap assembly
- extra DLLs: shared runtime implementation

Tile mods declare:

```json
"dependencies": ["tile-runtime"]
```

Pros:

- aligns with the existing dependency system
- one canonical runtime owner
- easy to reason about load order
- avoids duplicate patch owners

Cons:

- users install a runtime mod explicitly
- loader may still need better shared assembly resolution if dependent mods reference runtime types directly

## Option B: Shared library folder plus bootstrap shim

Install the shared runtime into a common libs folder and let tile mods reference it.

Pros:

- cleaner dependency story for mod authors
- avoids packaging the runtime as a visible mod

Cons:

- current framework does not appear to fully support this yet
- likely requires core loader changes for assembly resolution
- more moving parts than Option A

## Recommendation

Start with Option A. It fits the current framework best and avoids needing a loader redesign before proving the runtime split.

## Proposed API Shape

Do not make tile mods depend on `TerrariaModder.Core` owning tile registration anymore.

Instead expose a runtime-owned API:

```csharp
public static class TileRuntimeApi
{
    public static bool RegisterTile(string modId, string tileName, TileDefinition definition);
    public static int ResolveTile(string tileRef);
    public static bool TryGetTileType(string fullId, out int tileType);
}
```

Then provide a mod-facing convenience layer:

```csharp
public sealed class TileRuntimeContext
{
    public bool RegisterTile(string tileName, TileDefinition definition);
}
```

This can be created from:

- direct runtime bootstrap access
- an adapter in `TerrariaModder.Core`
- a helper package referenced by tile mods

## Internal Components To Extract

These pieces are natural runtime-owned candidates:

- `TileDefinition`
- `TileRegistry`
- `TileTypeExtension`
- `TileTextureLoader`
- `TileObjectRegistrar`
- `TileBehaviorPatches`
- `TileSavePatches`
- `PlayerAdjTileSafetyPatches`

Likely adapters or integration points:

- `AssetSystem` tile-specific orchestration
- `ModContext.RegisterTile(...)`
- recipe tile resolution hookup
- item `CreateTileId` resolution hookup

## Boundary Proposal

## Keep in Core

- generic mod discovery and loading
- generic lifecycle hook dispatch
- manifest dependency handling
- optional adapter methods that forward to tile runtime if present

## Move to Tile Runtime

- all custom tile data structures
- all tile Harmony patches
- all tile save/load interception
- all global tile resizing logic
- tile texture ownership
- tile container persistence

## Optional compatibility layer in Core

To reduce breakage, Core can keep:

```csharp
public bool RegisterTile(string tileName, TileDefinition definition)
{
    return TileRuntimeBridge.RegisterTile(Manifest.Id, tileName, definition);
}
```

That bridge should be optional and fail clearly if the shared runtime is not installed.

## Loading Strategy

## Phase 1: Mod initialization

Dependent tile mods register tile definitions only.

No patching here.

## Phase 2: Runtime bootstrap `OnGameReady`

The shared runtime:

- locks registration
- assigns deterministic runtime IDs across all registered tiles
- applies global array extension once
- applies Harmony patches once
- registers tile metadata once

## Phase 3: Runtime bootstrap `OnContentLoaded` / update

The shared runtime:

- injects textures
- retries delayed registration where Terraria startup timing requires it
- stabilizes texture slots

## Phase 4: World save/load

The shared runtime owns all sidecar save files and restoration logic.

This must remain centralized so multiple tile mods do not independently intercept the same world pipeline.

## Compatibility Risks

## 1. Shared assembly resolution

Biggest technical risk.

If dependent mods reference `TerrariaModder.TileRuntime.dll` directly, the framework needs a reliable way to load that same assembly instance for every mod.

Possible answers:

- add `AppDomain.AssemblyResolve` support in Core
- load shared libs from a known folder before loading mods
- package runtime API types inside a single runtime mod and ensure dependent mods compile against the same assembly identity

## 2. Type identity leakage

If mods compile against one copy of `TileDefinition` but runtime loads another, casts and method calls fail.

This is why "same DLL name copied into each mod folder" is not acceptable for the multi-mod design.

## 3. Lifecycle ownership

Only one bootstrap should apply global patches. Duplicate owners must detect and no-op or hard-fail with a clear log.

## 4. Save format ownership

Sidecar files like `.tiles.moddata` must stay versioned and runtime-owned. Do not let individual mods customize file naming or save interception independently.

## Migration Plan

## Step 1

Extract tile classes from Core into a new project without changing behavior.

Target:

- buildable runtime assembly
- no functional changes yet

## Step 2

Add a runtime bootstrap assembly/mod that exposes lifecycle hooks and owns patch application.

Target:

- single place calling runtime lifecycle methods

## Step 3

Replace direct Core tile ownership with a bridge.

Target:

- `ModContext.RegisterTile(...)` forwards to runtime if present
- clear error logging if absent

## Step 4

Solve shared assembly loading explicitly.

Target:

- one runtime assembly instance per process
- dependent mods can reference runtime API safely

## Step 5

Move docs and sample mods to the new runtime API.

Target:

- tile mods no longer depend on custom tile patches living in Core

## Decision Summary

The shared library/runtime approach is viable, but only if it is treated as a single shared runtime with one bootstrap owner.

The safest first implementation is:

1. create a `tile-runtime` dependency mod
2. move tile-global logic out of Core into that runtime
3. keep a small Core bridge only for compatibility
4. defer any "shared libs folder" loader redesign until after the runtime-mod approach is proven

## Immediate Next Work

1. Create the new runtime project skeletons:
   - `src/TileRuntime/`
   - `src/TileRuntime.Bootstrap/`
2. Define the minimal runtime API surface and bridge contract.
3. Decide the assembly-loading model before moving API types like `TileDefinition`.
4. Prototype registration and one patch path end-to-end before migrating save/load.

## Scaffold Status

Initial scaffolding has been added on `feature/shared-tile-runtime-plan`:

- `src/TileRuntime/`
- `src/TileRuntimeBootstrap/`
- `src/Core/Runtime/TileRuntimeBridge.cs`
- `src/Core/Runtime/ITileRuntimeBridge.cs`

Current status:

- the shared runtime owns a minimal registration API and lifecycle entry points
- the bootstrap mod owns injector-visible lifecycle hooks
- Core now has an optional bridge contract for future forwarding

Deliberate limitations of the scaffold:

- no loader-level shared assembly resolution yet
- no migration of the existing tile implementation yet
- no compatibility adapter from `TerrariaModder.Core.Assets.TileDefinition` yet
- placeholder runtime tile IDs are used only to prove API flow

## Zero-Core Audit

Current state on this branch:

- tile-global runtime behavior has been moved into `TerrariaModder.TileRuntime`
- the only Core changes introduced by this branch are optional bridge helpers in:
  - `src/Core/Assets/AssetSystem.cs`
  - `src/Core/ModContext.cs`

That means the remaining path to a fresh upstream Core is now primarily a mod API migration problem, not a runtime ownership problem.

To remove the last Core deltas:

1. register tiles through `TerrariaModder.TileRuntime` directly
2. stop calling `ModContext.RegisterTile(...)`
3. remove the temporary Core bridge changes

Remaining blockers before claiming full zero-Core support:

- validate runtime save/load ordering against a fresh upstream Core install
- confirm no tile mod still depends on Core-side tile APIs or tile definitions
- test a real multi-tile/container mod end-to-end against the runtime mod only
