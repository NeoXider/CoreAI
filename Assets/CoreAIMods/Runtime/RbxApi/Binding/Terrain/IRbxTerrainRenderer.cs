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
