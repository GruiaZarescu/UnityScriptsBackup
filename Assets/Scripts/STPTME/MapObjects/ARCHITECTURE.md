# Map Object Rendering Architecture (Restructured)

## Overview

Unified, clean pipeline for rendering map objects across all LODs using both data sources (cell objects and blobs).

```
┌─────────────────────────────────────────────────────────────────┐
│                      ORCHESTRATOR                                │
│                 ChunkObjectLoader                                │
│  Unified routing logic per data item based on registry rules     │
└─────────────┬──────────────────────────────────┬────────────────┘
              │                                  │
       ┌──────▼────────────┐          ┌──────────▼──────────┐
       │  Cell Objects     │          │  Blobs              │
       │  CellObjectReader │          │  CellBlotchReader   │
       └──────┬────────────┘          └──────────┬──────────┘
              │                                  │
              └──────────────┬───────────────────┘
                             │
                    ┌────────▼─────────┐
                    │ Registry Lookup  │
                    │ IsInstancedAtLOD │
                    └────────┬─────────┘
                             │
            ┌────────────────┼────────────────┐
            │                │                │
    ┌───────▼────────┐ ┌─────▼─────┐ ┌──────▼──────┐
    │ LOD0 GameObject│ │ LOD1+ both │ │GPU Instance │
    │ (Prefab spawn) │ │(mixed)     │ │(Blob upload)│
    └────────────────┘ └───────────┘ └─────────────┘
            │                │                │
            └────────────────┼────────────────┘
                             │
                    ┌────────▼─────────┐
                    │  MapPrefabStreamer
                    │   (LOD0 objects)  │
                    │  ImpostorRenderer │
                    │   (GPU blobs)     │
                    └───────────────────┘
```

## Data Flow

### 1. Cell Objects
- **Source**: `CellObjectReader` reads pre-baked object data from group cell files
- **Format**: Singular instances with position, rotation, scale, prototypeIndex
- **Typical use**: Trees baked with exact positions (not clusters)
- **LOD routing**:
  - LOD0 + `!shouldInstance` → Spawn GameObject (via `MapPrefabStreamer`)
  - LOD0 + `shouldInstance` → (rare, should spawn GameObject with collider anyway)
  - LOD1+ + `!shouldInstance` → Spawn GameObject
  - LOD1+ + `shouldInstance` → Convert to GPU format (rare for cell objects)

### 2. Blobs
- **Source**: `CellBlotchReader` reads procedural data (center + radius + density)
- **Format**: `BlotchData` struct containing:
  - Position (quantized local coordinates, converts to world via sphere projection)
  - `prototypeIndex` (bits 8-15 of packedMeta)
  - Radius & density for procedural placement
  - `instanceAlways` flag (bit 25 of packedMeta) — forces GPU instancing even at LOD0
- **Typical use**: Grass clusters, procedural forests, dense foliage
- **LOD routing**:
  - If single-instance blob (`radius ≈ 0, density ≈ 1`):
    - LOD0 + `!instanceAlways` → Spawn GameObject (via `BlobConverter` + `MapPrefabStreamer`)
    - LOD0 + `instanceAlways` → Keep for GPU (rare)
    - LOD1+ + `shouldInstance` → Add to GPU buffer
  - If cluster (`radius > 0 or density > 1`):
    - LOD1+ + `shouldInstance` → Add to GPU buffer
    - LOD1+ + `!shouldInstance` → (rare, skip or warn)

### 3. Registry Decision Logic
Every data item queries `MapObjectPrototypeEntry.IsInstancedAtLOD(lod)`:
```csharp
public bool IsInstancedAtLOD(int chunkLOD)
{
    if (!IsValid) return false;
    if (instanceAlways) return true;           // Always instance
    if (chunkLOD == 0) return false;           // LOD0: never instance (spawn GameObject)
    return shouldInstance;                     // LOD1+: instance if shouldInstance=true
}
```

## Component Responsibilities

### ChunkObjectLoader (Orchestrator)
**Purpose**: Central routing logic, unified data handling

**Responsibilities**:
1. Subscribe to `ChunkRegistry.OnChunkCreated` / `OnChunkRemoved` events
2. Load cell objects from `CellObjectReader` per chunk
3. Load blobs from `CellBlotchReader` per chunk
4. For each data item:
   - Query registry for prototype settings
   - Decide: spawn GameObject or accumulate for GPU
5. Spawn GameObjects via `MapPrefabStreamer`
6. Accumulate GPU-eligible blobs and submit to `ImpostorRenderer` at startup

**Public API**:
- `Initialize()` — Set up readers and caches
- `HandleChunkCreated(int packed, FaceId face, byte lod)` — Route data for chunk
- `HandleChunkRemoved(int packed, FaceId face, byte lod)` — Clean up chunk

### MapPrefabStreamer (LOD0 Object Manager)
**Purpose**: Decouple object spawning from orchestration logic

**Responsibilities**:
1. Object pooling (optional, configurable)
2. Hierarchical organization (parent per chunk)
3. Batch spawn / despawn operations
4. Automatic cleanup on chunk removal

**Features**:
- Configurable pool size per prototype
- Hierarchy: Root → Chunk parent → Individual objects
- Optional attachment of `MapObjectMetadata` for runtime queries
- Destruction and pool return on chunk removal

**Public API**:
- `SpawnObject(prototypeIndex, chunkPacked, face, lod, worldPos, rotation, scale, seed) → GameObject`
- `DespawnChunkObjects(chunkPacked, face, lod)`
- `ClearAll()`

### BlobConverter (Data Bridge)
**Purpose**: Convert between blob and object representations

**Responsibilities**:
1. Detect single-instance blobs vs clusters
2. Calculate world position from blob quantized coords
3. Create pseudo-cell-objects from blobs for consistent handling

**Public API**:
- `IsSingleInstance(blob) → bool`
- `IsCluster(blob) → bool`
- `CalculateBlotchWorldPosition(blob, ...) → Vector3`
- `PseudoCellObject.FromBlob(blob, worldPos)`

### CellBlotchReader (Blob Data Source)
**Purpose**: Load and query blob data

**Responsibilities**:
1. Load all blobs from group cell files
2. Build efficient per-chunk query indices
3. Cache blobs in memory for fast access

**Public API** (new):
- `InitializeGlobalCache(cellsFolder, heightmapSubPow2, minX)` — One-time setup
- `GetBlobsForChunk(chunkPacked) → List<BlotchData>`
- `GetAllBlotches() → BlotchData[]`
- `TotalBlobCount() → int`

### ImpostorRenderer (GPU Instancing)
**Purpose**: GPU-based rendering for LOD1+ instanced objects

**Changes**:
1. Receives GPU-eligible blobs from `ChunkObjectLoader` (instead of loading directly)
2. Blobs have been pre-filtered by registry rules
3. All blobs in the buffer are marked `shouldInstance=true` at their LOD

**Public API** (updated):
- `Initialize(registry, sphereCenter, sphereRadius, eligibleBlotches[], chunkData[], ...)`

## Execution Timeline

### Startup (Scene Load)
1. `ChunkObjectLoader.Start()` called
   - Initialize `CellObjectReader`
   - Initialize `CellBlotchReader` with global cache
   - Initialize `MapPrefabStreamer` with object pools
   - Subscribe to `ChunkRegistry` events
2. `ChunkManager` starts spawning chunks
   - Each chunk triggers `OnChunkCreated` → `HandleChunkCreated`

### Per-Chunk Load (OnChunkCreated)
1. Orchestrator loads cell objects from file
2. Orchestrator loads blobs from cache
3. For each cell object:
   - Query registry for `entry.IsInstancedAtLOD(lod)`
   - Spawn GameObject or skip (already GPU instanced, should be rare)
4. For each blob:
   - Query registry for `entry.IsInstancedAtLOD(lod)`
   - If single-instance + LOD0 + `!instanceAlways` → Spawn GameObject
   - Else if GPU-eligible → Accumulate in `_gpuBlotches` list
5. On first chunk creation: Submit accumulated GPU blobs to `ImpostorRenderer`

### Per-Chunk Unload (OnChunkRemoved)
1. Orchestrator calls `MapPrefabStreamer.DespawnChunkObjects(packed, face, lod)`
2. Streamer returns objects to pool or destroys them
3. Chunk parent destroyed

## Configuration

### MapObjectPrototypeRegistry Entry Settings
Each prototype entry defines its rendering behavior:

| Setting | Type | Meaning |
|---------|------|---------|
| `name` | string | Display name |
| `shouldInstance` | bool | Use GPU instancing for LOD1+ |
| `instanceAlways` | bool | Force GPU instancing even at LOD0 (only if `shouldInstance=true`) |
| `sourcePrefab` | GameObject | Prefab to spawn at LOD0 (or all LODs if `shouldInstance=false`) |
| `lodMeshes[0..N]` | Mesh[] | Meshes for GPU instancing (LOD0 is highest detail) |
| `material` | Material | Shared material for GPU instances (must have GPU instancing enabled) |
| `baseWidth`, `baseHeight` | float | Base dimensions for visual scaling |
| `blotchRadius`, `blotchDensity` | float | Procedural parameters for clusters |
| `conflictCategory` | byte | For grid competition (1=Grass, 2=Canopy, 4=Trunk) |

### Typical Tree Setup
```
Entry: OakTree
  shouldInstance = true
  instanceAlways = false
  sourcePrefab = OakTree_LOD0_Prefab (with collider, physics)
  lodMeshes = [OakTree_LOD0, OakTree_LOD1, OakTree_LOD2, OakTree_Billboard]
  material = TreeInstanced_Material
  blotchRadius = 0 (single instances)
  blotchDensity = 1 (one per instance)
```

**Behavior**:
- LOD0: Spawn `sourcePrefab` (interactive, with collider)
- LOD1+: GPU instance using `lodMeshes` + `material`

### Typical Grass Setup
```
Entry: DenseGrass
  shouldInstance = true
  instanceAlways = true
  sourcePrefab = null (ignored)
  lodMeshes = [GrassTuft, GrassBillboard]
  material = GrassInstanced_Material
  blotchRadius = 5.0 (5m clusters)
  blotchDensity = 10 (10 per sqm)
```

**Behavior**:
- LOD0: GPU instance (no colliders needed)
- LOD1+: GPU instance
- Blobs expanded into individual instances via procedural placement

## Blob-to-GPU Conversion

When a blob is GPU-eligible, it must be converted to the format expected by `ImpostorRenderer`:

1. **BlotchData** → remains as-is in the buffer
2. **GPU Compute Shader** (CSExpandBlotches) expands blobs into individual instances
3. Each expanded instance stored as **InstanceData**:
   ```csharp
   struct InstanceData
   {
       float3 worldPos;      // World position
       uint packedMeta;      // prototypeIndex | chunkLOD | rotation | scale
       uint seed;            // Deterministic variation seed
   }
   ```

## Filtering Rules (Summary)

**Cell Objects:**
- LOD0 always spawn GameObjects (regardless of `shouldInstance`)
- LOD1+ with `shouldInstance=true` are skipped (they shouldn't be in cell objects anyway)
- LOD1+ with `shouldInstance=false` spawn GameObjects

**Blobs:**
- Single-instance blobs at LOD0 with `!instanceAlways` spawn GameObjects
- All other blobs eligible for GPU go to the buffer
- Clusters always GPU (too many to spawn as GameObjects)

## Future Enhancements

1. **Dynamic Blob Removal**: Remove specific blobs when chunks unload (requires persistent GPU buffer)
2. **Async Prefab Loading**: Use addressables or custom SRP integration
3. **Per-chunk LOD Config**: Allow per-chunk override of LOD thresholds
4. **Blob Streaming**: Load blobs on-demand instead of all at startup
5. **Blob Respawning**: Convert LOD1+ GameObjects back to GPU on reload
