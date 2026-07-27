using System;
using System.Collections.Generic;
using UnityEngine;
using CustomTypes;

namespace STPTME.MapObjects
{
    /// <summary>Uniform per-instance shape both sources hand to ChunkObjectLoader.</summary>
    public struct SourcedObjectInstance
    {
        public byte prototypeIndex;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
        /// <summary>Database entry id, or 0 if this instance came from a baked file
        /// (baked files carry no live identity — there's nothing to remove/update at runtime).</summary>
        public ulong mapObjectId;
    }

    /// <summary>
    /// Abstraction over "where do standalone map object instances for this chunk come from."
    /// ChunkObjectLoader talks only to this interface — it never knows whether it's reading
    /// shipped baked files or a live authoring database.
    /// </summary>
    public interface IMapObjectSource
    {
        ArraySegment<SourcedObjectInstance> GetObjectsForChunk(int packed, FaceId face, int numberOfChunks, byte lodLevel);
    }

    /// <summary>Production path: reads the shipped, baked CellObjectGroup_*.bytes files.</summary>
    public class BakedFileObjectSource : IMapObjectSource
    {
        private readonly CellObjectReader _reader;
        private SourcedObjectInstance[] _convertBuffer = Array.Empty<SourcedObjectInstance>();

        public BakedFileObjectSource(CellObjectReader reader) { _reader = reader; }

        public ArraySegment<SourcedObjectInstance> GetObjectsForChunk(int packed, FaceId face, int numberOfChunks, byte lodLevel)
        {
            var seg = _reader.GetObjectsForChunk(packed, face, numberOfChunks, lodLevel);
            if (seg.Count == 0) return default;

            if (_convertBuffer.Length < seg.Count)
                _convertBuffer = new SourcedObjectInstance[seg.Count * 2];

            for (int i = 0; i < seg.Count; i++)
            {
                var src = seg.Array[seg.Offset + i];
                _convertBuffer[i] = new SourcedObjectInstance
                {
                    prototypeIndex = src.prototypeIndex,
                    position = src.position,
                    rotation = src.rotation,
                    scale = src.scale,
                    mapObjectId = 0
                };
            }
            return new ArraySegment<SourcedObjectInstance>(_convertBuffer, 0, seg.Count);
        }
    }

    /// <summary>
    /// Editor-authoring path: reads live from a MapObjectDatabase, so placing/removing objects
    /// in play mode shows up immediately through the exact same streaming pipeline shipped
    /// builds use — no separate edit-mode renderer needed.
    ///
    /// Per-chunk index is rebuilt only when MapObjectDatabase.Version changes, not every call.
    /// </summary>
    public class LiveDatabaseObjectSource : IMapObjectSource
    {
        private readonly MapObjectDatabase _database;
        private readonly Vector3 _sphereCenter;
        private readonly float _chunkSize;
        private readonly float _faceWorldSize;
        private readonly int _numberOfChunks;
        private readonly sbyte _minX, _maxX;

        private Dictionary<(int packed, FaceId face), List<SourcedObjectInstance>> _byChunk;
        private int _cachedVersion = -1;
        private SourcedObjectInstance[] _filterBuffer = Array.Empty<SourcedObjectInstance>();

        public LiveDatabaseObjectSource(MapObjectDatabase database, Vector3 sphereCenter,
            float chunkSize, float faceWorldSize, int numberOfChunks, sbyte minX, sbyte maxX)
        {
            _database = database;
            _sphereCenter = sphereCenter;
            _chunkSize = chunkSize;
            _faceWorldSize = faceWorldSize;
            _numberOfChunks = numberOfChunks;
            _minX = minX;
            _maxX = maxX;
        }

        private void RebuildIfNeeded()
        {
            if (_database == null) return;
            if (_byChunk != null && _cachedVersion == _database.Version) return;

            _byChunk = new Dictionary<(int, FaceId), List<SourcedObjectInstance>>();
            foreach (var entry in _database.All)
            {
                if (!MapObjectChunkMath.TryResolve(entry.worldPosition, _sphereCenter, _chunkSize,
                    _faceWorldSize, _numberOfChunks, _minX, _maxX, out var addr))
                    continue;

                var key = (addr.packed, addr.face);
                if (!_byChunk.TryGetValue(key, out var list))
                {
                    list = new List<SourcedObjectInstance>();
                    _byChunk[key] = list;
                }
                list.Add(new SourcedObjectInstance
                {
                    prototypeIndex = (byte)entry.prototypeIndex,
                    position = entry.worldPosition,
                    rotation = entry.worldRotation,
                    scale = entry.localScale,
                    mapObjectId = entry.id
                });
            }
            _cachedVersion = _database.Version;
        }

        public ArraySegment<SourcedObjectInstance> GetObjectsForChunk(int packed, FaceId face, int numberOfChunks, byte lodLevel)
        {
            RebuildIfNeeded();

            // Parity note: the CURRENT baked file format always writes lodLevel=0 for every
            // standalone object (see MapObjectBaker.WriteGroupFile) — objects aren't actually
            // LOD-tagged today, they just only ever match lodLevel==0 requests. This mirrors
            // that exact behavior rather than silently introducing new semantics. If you later
            // want standalone objects to persist across all LODs, both this method and the
            // baked format need to change together.
            if (lodLevel != 0 || _byChunk == null || !_byChunk.TryGetValue((packed, face), out var list) || list.Count == 0)
                return default;

            if (_filterBuffer.Length < list.Count)
                _filterBuffer = new SourcedObjectInstance[list.Count * 2];
            list.CopyTo(_filterBuffer);
            return new ArraySegment<SourcedObjectInstance>(_filterBuffer, 0, list.Count);
        }
    }
}