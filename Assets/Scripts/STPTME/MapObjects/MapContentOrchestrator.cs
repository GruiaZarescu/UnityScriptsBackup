using System;
using System.Collections.Generic;
using UnityEngine;
using CustomTypes;
using STPTME.MapObjects;

namespace STPTME.MapObjects
{
    /// <summary>
    /// The single decision point ("black box") for whether a piece of authored content —
    /// terrain-authored blotch or placed map object — belongs in the GPU-instanced buffer,
    /// the CPU prefab-streaming path, or both. Runs ONCE, at scene load, before
    /// ImpostorRenderer.Initialize builds its (fixed-size, upload-once) buffer.
    ///
    /// Rules, driven entirely by MapObjectPrototypeRegistry per-prototype flags:
    ///   !shouldInstance                    -> prefab only, every LOD, forever. Never GPU.
    ///   shouldInstance && !instanceAlways   -> prefab at LOD0, GPU at LOD1+ (the tree case).
    ///   shouldInstance && instanceAlways    -> GPU only, every LOD. Never a prefab.
    ///
    /// Terrain blotches keep whatever radius/density they were authored with. Map objects
    /// are ALWAYS forced to single-instance (radius=0, density=1) when converted — there is
    /// no such thing as "a cluster of one placed fence" — this is by design, not a
    /// misconfiguration, so it is never warned about.
    ///
    /// The one thing that IS a misconfiguration and IS warned about: a terrain blotch that
    /// is a cluster (radius>0 or density>1) but whose prototype has instanceAlways=false.
    /// There is no CPU-side cluster expansion — such a blotch would spawn exactly one prefab
    /// representing what should be many instances. These are dropped from both paths, with
    /// a warning identifying exactly which prototype needs instanceAlways=true.
    /// </summary>
    public static class MapContentOrchestrator
    {
        public readonly struct Result
        {
            /// <summary>Final, pre-pruned contents for the single GPU blotch buffer —
            /// GPU-eligible terrain blotches, unchanged, plus GPU-eligible map objects,
            /// converted to single-instance blotches.</summary>
            public readonly BlotchData[] GpuBlotches;

            /// <summary>Every VALID terrain blotch (misconfigured ones already dropped,
            /// with a warning), keyed by (chunkPacked, face) — NOT chunkPacked alone, since
            /// chunkPacked does not encode face and different faces can reuse the same
            /// (mapX,mapY,chunkX,chunkY) coordinates. Replaces CellBlotchQuery's separate,
            /// redundant full reload; ProcessBlobs queries this instead.</summary>
            public readonly Dictionary<(int packed, FaceId face), List<BlotchData>> TerrainBlotchesByChunk;

            public Result(BlotchData[] gpuBlotches, Dictionary<(int, FaceId), List<BlotchData>> byChunk)
            {
                GpuBlotches = gpuBlotches;
                TerrainBlotchesByChunk = byChunk;
            }
        }

        public static Result Build(
            BlotchData[] terrainBlotches,
            List<(int chunkPacked, FaceId face, CellObjectReader.CellObjectInstance instance)> mapObjects,
            MapObjectPrototypeRegistry registry,
            Vector3 sphereCenter,
            float chunkSizeMeters,
            float faceWorldSize,
            int numberOfChunks,
            sbyte minX, sbyte maxX)
        {
            var gpuList = new List<BlotchData>((terrainBlotches?.Length ?? 0) + (mapObjects?.Count ?? 0));
            var byChunk = new Dictionary<(int, FaceId), List<BlotchData>>();

            var warnedMissingEntry = new HashSet<int>();
            var warnedClusterMisconfig = new HashSet<int>();
            int droppedMisconfigured = 0;
            int droppedNoEntry = 0;
            int convertedObjects = 0;
            int prefabOnlyObjects = 0;
            int unresolvedObjects = 0;

            // ── Terrain blotches ──────────────────────────────────────────────
            if (terrainBlotches != null)
            {
                foreach (var blotch in terrainBlotches)
                {
                    var entry = registry.GetEntry(blotch.PrototypeIndex);
                    if (entry == null)
                    {
                        if (warnedMissingEntry.Add(blotch.PrototypeIndex))
                            Debug.LogWarning($"[MapContentOrchestrator] Terrain blotch references " +
                                $"prototypeIndex={blotch.PrototypeIndex}, which has no registry entry. Dropped.");
                        droppedNoEntry++;
                        continue;
                    }

                    if (BlobConverter.IsCluster(blotch) && !entry.instanceAlways)
                    {
                        if (warnedClusterMisconfig.Add(blotch.PrototypeIndex))
                            Debug.LogWarning(
                                $"[MapContentOrchestrator] Prototype '{entry.name}' (index {blotch.PrototypeIndex}) " +
                                $"has a cluster blotch (radius={blotch.RadiusMeters:F2}m, density={blotch.DensityPerSqM:F2}/m²) " +
                                $"but instanceAlways=false. There is no CPU-side cluster expansion — spawning this as a " +
                                $"prefab would render exactly ONE instance where many were intended. " +
                                $"Set instanceAlways=true for this prototype. Dropped (example at chunk=0x{blotch.chunkPacked:X8}, face={blotch.Face}).");
                        droppedMisconfigured++;
                        continue; // misconfigured -> excluded from BOTH the GPU buffer and the prefab index
                    }

                    var key = (blotch.chunkPacked, blotch.Face);
                    if (!byChunk.TryGetValue(key, out var list))
                    {
                        list = new List<BlotchData>();
                        byChunk[key] = list;
                    }
                    list.Add(blotch);

                    if (entry.shouldInstance)
                        gpuList.Add(blotch);
                }
            }

            // ── Map objects ───────────────────────────────────────────────────
            if (mapObjects != null)
            {
                foreach (var (_, _, instance) in mapObjects)
                {
                    var entry = registry.GetEntry(instance.prototypeIndex);
                    if (entry == null)
                    {
                        if (warnedMissingEntry.Add(instance.prototypeIndex))
                            Debug.LogWarning($"[MapContentOrchestrator] Map object references " +
                                $"prototypeIndex={instance.prototypeIndex}, which has no registry entry. Skipped.");
                        droppedNoEntry++;
                        continue;
                    }

                    if (!entry.shouldInstance)
                    {
                        // Prefab-only. ProcessCellObjects already handles this via its own
                        // lazy per-chunk read (IMapObjectSource) — nothing to do here.
                        prefabOnlyObjects++;
                        continue;
                    }

                    if (!MapObjectChunkMath.TryResolve(instance.position, sphereCenter, chunkSizeMeters,
                            faceWorldSize, numberOfChunks, minX, maxX, out var addr))
                    {
                        Debug.LogWarning($"[MapContentOrchestrator] Could not resolve chunk address for a " +
                            $"GPU-eligible map object (prototype {instance.prototypeIndex}, pos={instance.position}). Skipped.");
                        unresolvedObjects++;
                        continue;
                    }

                    // Deterministic-enough seed for GPU wind/variation hashing. Doesn't need
                    // to survive across sessions with bit-exact stability, only to be stable
                    // for the lifetime of this loaded buffer.
                    uint seed = (uint)(instance.position.GetHashCode() ^ (instance.prototypeIndex * 2654435761u));

                    BlotchData converted = MapObjectToBlotchConversion.ConvertToBlotch(
                        addr.packed, addr.face, (byte)instance.prototypeIndex, entry.conflictCategory,
                        instance.position, instance.rotation, sphereCenter,
                        addr.localXMeters, addr.localZMeters, chunkSizeMeters, seed);

                    gpuList.Add(converted);
                    convertedObjects++;
                }
            }

            Debug.Log($"[MapContentOrchestrator] Built GPU buffer: {gpuList.Count} blotches total " +
                $"({gpuList.Count - convertedObjects} from terrain, {convertedObjects} converted from objects). " +
                $"Terrain prefab-index: {byChunk.Count} chunk buckets. " +
                $"Dropped: {droppedMisconfigured} misconfigured cluster(s), {droppedNoEntry} missing-registry-entry, " +
                $"{unresolvedObjects} unresolved object(s). Prefab-only objects (unaffected, handled elsewhere): {prefabOnlyObjects}.");

            return new Result(gpuList.ToArray(), byChunk);
        }
    }
}