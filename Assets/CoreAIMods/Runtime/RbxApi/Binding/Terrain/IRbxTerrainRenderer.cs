using System;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding.Terrain
{
    /// <summary>
    /// Answers whether a Unity ray hit belongs to the Terrain, for <c>workspace:Raycast</c>: the
    /// physics port asks it about every collider the binder does not know.
    /// </summary>
    public interface IRbxTerrainHitResolver
    {
        /// <summary>
        /// True when <paramref name="collider"/> is a Terrain chunk collider and the cell behind the
        /// hit face is solid; then <paramref name="terrainId"/> is the Terrain instance, and
        /// <paramref name="material"/> and <paramref name="normalStuds"/> describe the cell face that
        /// was hit (Roblox space, the normal snapped to the face axis).
        /// </summary>
        bool TryResolveHit(Collider collider, Vector3 pointUnity, Vector3 normalUnity, out InstanceId terrainId,
            out RbxMaterialId material, out RbxVector3 normalStuds);
    }

    /// <summary>
    /// Builds chunk colliders out of turn, so a body never falls through Terrain whose collider is still
    /// queued behind the per-frame budget. The physics port asks it about every simulated body each fixed
    /// step, before gravity moves the body.
    /// </summary>
    public interface IRbxTerrainColliderReadiness
    {
        /// <summary>
        /// Re-meshes now, outside the frame budget, every queued chunk whose faces or collider will change
        /// and whose cells overlap <paramref name="unityBounds"/>; returns the chunks built.
        /// </summary>
        int EnsureColliders(Bounds unityBounds);

        /// <summary>
        /// As <see cref="EnsureColliders(Bounds)"/>, but builds at most <paramref name="maxBuilds"/> chunks, nearest
        /// the box's centre first, so a body moving into a field of queued chunks never stalls a frame.
        /// </summary>
        int EnsureColliders(Bounds unityBounds, int maxBuilds);

        /// <summary>
        /// Builds, in the order a ray from <paramref name="origin"/> along <paramref name="direction"/> crosses them,
        /// at most <paramref name="maxBuilds"/> queued chunks within <paramref name="distance"/> metres.
        /// </summary>
        int EnsureCollidersAlong(Vector3 origin, Vector3 direction, float distance, int maxBuilds);

        /// <summary>True while a chunk overlapping <paramref name="unityBounds"/> waits for its faces or collider.</summary>
        bool IsGeometryPendingIn(Bounds unityBounds);
    }

    /// <summary>
    /// Answers whether a Terrain cell is solid, so the physics port can lift a character whose feet ended up
    /// inside a block (a block placed on it, a collider built under it a step late) back on top.
    /// </summary>
    public interface IRbxTerrainSolidCells
    {
        /// <summary>True when cell (x, y, z) holds a material other than Air and Water.</summary>
        bool IsSolidCell(int x, int y, int z);
    }

    /// <summary>Answers whether a Terrain cell is water: for swimming and for the look under water.</summary>
    public interface IRbxTerrainWaterCells
    {
        /// <summary>True when cell (x, y, z) holds Water.</summary>
        bool IsWaterCell(int x, int y, int z);
    }

    /// <summary>
    /// The Unity face of one Terrain: turns changed chunks into rendered, colliding meshes under a
    /// per-frame budget. The default is <see cref="RbxTerrainRenderer"/>; a host that wants another look
    /// (a texture-array material, smooth meshing) supplies its own through
    /// <see cref="RbxTerrainPresenter.RendererFactory"/>.
    /// </summary>
    public interface IRbxTerrainRenderer : IRbxTerrainHitResolver, IDisposable
    {
        /// <summary>The Terrain this renderer draws.</summary>
        RbxTerrain Terrain { get; }

        /// <summary>Chunks waiting for a re-mesh.</summary>
        int PendingChunkCount { get; }

        /// <summary>
        /// Re-meshes waiting chunks, oldest first: at least one when any is waiting, then more while
        /// fewer than <paramref name="maxChunks"/> ran and less than <paramref name="maxMilliseconds"/>
        /// has passed. Returns the chunks processed.
        /// </summary>
        int ProcessDirty(int maxChunks, double maxMilliseconds);
    }
}
