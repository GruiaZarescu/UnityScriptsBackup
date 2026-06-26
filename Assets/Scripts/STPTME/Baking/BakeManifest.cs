using UnityEngine;
using System.IO;
using CustomTypes;

#if UNITY_EDITOR
using UnityEditor;

/// <summary>
/// Per-terrain change-tracking manifest for incremental baking.
///
/// Stores AssetDatabase.GetAssetDependencyHash (content-based, O(1)) and an FNV1a
/// hash of the tree instances. MeshSaver checks both before processing a terrain;
/// if both match the manifest entry, the terrain's cells are reused as-is.
///
/// The dependency hash survives scene object deletion/re-creation because it is
/// derived from the .asset file content, not the file timestamp or scene hierarchy.
///
/// Manifest file is written to StreamingAssets/MapAssets/BakeManifest.bytes
/// at the end of each successful bake.
/// </summary>
public static class BakeManifest
{
    public const uint MAGIC = 0x424D4645; // "BMFE"
    public const ushort VERSION = 1;

    public const string MANIFEST_PATH = "MapAssets/BakeManifest.bytes";

    public struct Entry
    {
        public FaceId face;
        public byte terrainGridX;
        public byte terrainGridY;
        public ulong contentHashLo;   // AssetDatabase.GetAssetDependencyHash — lower 64 bits
        public ulong contentHashHi;   // upper 64 bits
        public uint treeHash;         // FNV1a over treeInstances (catches unsaved editor edits)
    }

    /// <summary>
    /// Loads the manifest from disk, or returns an empty array if no manifest exists.
    /// </summary>
    public static Entry[] Load()
    {
        string path = Path.Combine(Application.streamingAssetsPath, MANIFEST_PATH);
        if (!File.Exists(path))
            return System.Array.Empty<Entry>();

        try
        {
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                uint magic = reader.ReadUInt32();
                if (magic != MAGIC) return System.Array.Empty<Entry>();

                ushort version = reader.ReadUInt16();
                if (version != VERSION) return System.Array.Empty<Entry>();

                int count = reader.ReadInt32();
                var entries = new Entry[count];
                for (int i = 0; i < count; i++)
                {
                    entries[i] = new Entry
                    {
                        face = (FaceId)reader.ReadByte(),
                        terrainGridX = reader.ReadByte(),
                        terrainGridY = reader.ReadByte(),
                        contentHashLo = reader.ReadUInt64(),
                        contentHashHi = reader.ReadUInt64(),
                        treeHash = reader.ReadUInt32(),
                    };
                }
                return entries;
            }
        }
        catch
        {
            return System.Array.Empty<Entry>();
        }
    }

    /// <summary>
    /// Writes the manifest to disk. Thread-safe: uses lock for concurrent writes.
    /// </summary>
    private static readonly object saveLock = new object();
    
    public static void Save(Entry[] entries)
    {
        lock (saveLock)
        {
            string dir = Path.Combine(Application.streamingAssetsPath, "MapAssets");
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string path = Path.Combine(Application.streamingAssetsPath, MANIFEST_PATH);
            
            // Write to temp file first for atomicity
            string tempPath = path + ".tmp";
            using (var writer = new BinaryWriter(File.Open(tempPath, FileMode.Create)))
            {
                writer.Write(MAGIC);
                writer.Write(VERSION);
                writer.Write(entries.Length);
                foreach (var e in entries)
                {
                    writer.Write((byte)e.face);
                    writer.Write(e.terrainGridX);
                    writer.Write(e.terrainGridY);
                    writer.Write(e.contentHashLo);
                    writer.Write(e.contentHashHi);
                    writer.Write(e.treeHash);
                }
            }
            
            // Atomic move
            if (File.Exists(path)) File.Delete(path);
            File.Move(tempPath, path);
        }
    }

    /// <summary>
    /// Computes an FNV1a hash over a TerrainData's tree instances.
    /// Only depends on (prototypeIndex, position.x, position.z) so it catches
    /// add/remove/reposition of trees deterministically.
    /// </summary>
    public static uint HashTrees(TerrainData td)
    {
        var trees = td.treeInstances;
        if (trees == null || trees.Length == 0)
            return 0;

        uint hash = 2166136261u; // FNV offset basis
        foreach (var tree in trees)
        {
            hash ^= (uint)tree.prototypeIndex;
            hash *= 16777619u;

            uint x = (uint)System.BitConverter.SingleToInt32Bits(tree.position.x);
            hash ^= x;
            hash *= 16777619u;

            uint z = (uint)System.BitConverter.SingleToInt32Bits(tree.position.z);
            hash ^= z;
            hash *= 16777619u;
        }
        return hash;
    }

    /// <summary>
    /// Returns the AssetDatabase dependency hash for the terrain data's .asset file,
    /// encoded as two ulongs for serialization.
    /// </summary>
    public static void GetContentHashSplit(TerrainData td, out ulong lo, out ulong hi)
    {
        lo = 0; hi = 0;
        if (td == null) return;
        string path = AssetDatabase.GetAssetPath(td);
        if (string.IsNullOrEmpty(path)) return;
        Hash128 h = AssetDatabase.GetAssetDependencyHash(path);
        // Hash128.ToString() returns a 32-char hex string. Split into two 64-bit halves.
        string hex = h.ToString();
        if (hex.Length == 32)
        {
            lo = System.Convert.ToUInt64(hex.Substring(0, 16), 16);
            hi = System.Convert.ToUInt64(hex.Substring(16, 16), 16);
        }
    }

    /// <summary>
    /// Checks whether a terrain can be skipped (reuse last bake).
    /// Returns true if the content hash AND the tree hash both match an entry.
    /// Thread-safe: reads from shared entries array without modification.
    /// </summary>
    public static bool IsUnchanged(Terrain terrain, TerrainData td, FaceId face, byte gridX, byte gridY, Entry[] entries)
    {
        GetContentHashSplit(td, out ulong nowLo, out ulong nowHi);
        if (nowLo == 0 && nowHi == 0) return false;

        uint nowTrees = HashTrees(td);

        for (int i = 0; i < entries.Length; i++)
        {
            ref Entry e = ref entries[i];
            if (e.face == face && e.terrainGridX == gridX && e.terrainGridY == gridY)
            {
                return e.contentHashLo == nowLo && e.contentHashHi == nowHi && e.treeHash == nowTrees;
            }
        }

        return false; // No previous entry → must bake
    }
}

#endif
