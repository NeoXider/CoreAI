using System;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Terrain;

namespace CoreAI.Mods.Rbx.Instances.Building
{
    /// <summary>
    /// What lies under the cursor or the crosshair: the instance hit (a part, or the Terrain), where and on which face,
    /// how far from the camera, and for a Terrain hit the solid cell behind the face and the empty cell in front of it.
    /// </summary>
    public readonly struct RbxWorldPick : IEquatable<RbxWorldPick>
    {
        public RbxWorldPick(InstanceId instance, RbxVector3 position, RbxVector3 normal, double distanceStuds,
            bool isTerrain, bool hasSurface)
        {
            Instance = instance;
            Position = position;
            Normal = normal;
            DistanceStuds = distanceStuds;
            IsTerrain = isTerrain;
            HasSurface = hasSurface;
        }

        /// <summary>Nothing picked.</summary>
        public static RbxWorldPick None => default;

        /// <summary>The part or Terrain hit; <see cref="InstanceId.None"/> for no hit.</summary>
        public InstanceId Instance { get; }

        /// <summary>The hit point in studs; meaningful only when <see cref="HasSurface"/>.</summary>
        public RbxVector3 Position { get; }

        /// <summary>The face normal of the hit; meaningful only when <see cref="HasSurface"/>.</summary>
        public RbxVector3 Normal { get; }

        /// <summary>Camera-to-hit distance, studs.</summary>
        public double DistanceStuds { get; }

        /// <summary>True when the Terrain was hit.</summary>
        public bool IsTerrain { get; }

        /// <summary>False for a pick source that reports only the instance (the headless default).</summary>
        public bool HasSurface { get; }

        /// <summary>True when something was picked.</summary>
        public bool IsHit => Instance.IsValid;

        /// <summary>The face normal snapped to its main axis (a unit vector along X, Y or Z).</summary>
        public RbxVector3 AxisNormal => SnapToAxis(Normal);

        /// <summary>For a Terrain hit, the solid cell whose face was hit, as whole cell numbers.</summary>
        public RbxVector3 Cell
        {
            get
            {
                RbxVector3 axis = AxisNormal;
                return RbxTerrain.WorldToCell(new RbxVector3(Position.X - axis.X * 0.5f, Position.Y - axis.Y * 0.5f,
                    Position.Z - axis.Z * 0.5f));
            }
        }

        /// <summary>For a Terrain hit, the empty cell in front of the hit face.</summary>
        public RbxVector3 PlaceCell
        {
            get
            {
                RbxVector3 cell = Cell;
                RbxVector3 axis = AxisNormal;
                return new RbxVector3(cell.X + axis.X, cell.Y + axis.Y, cell.Z + axis.Z);
            }
        }

        /// <summary><paramref name="normal"/> as the unit vector of its largest component (zero stays zero).</summary>
        public static RbxVector3 SnapToAxis(RbxVector3 normal)
        {
            float x = Math.Abs(normal.X);
            float y = Math.Abs(normal.Y);
            float z = Math.Abs(normal.Z);
            if (x == 0f && y == 0f && z == 0f)
            {
                return new RbxVector3(0f, 0f, 0f);
            }

            if (x >= y && x >= z)
            {
                return new RbxVector3(Math.Sign(normal.X), 0f, 0f);
            }

            return y >= z ? new RbxVector3(0f, Math.Sign(normal.Y), 0f) : new RbxVector3(0f, 0f, Math.Sign(normal.Z));
        }

        public bool Equals(RbxWorldPick other)
        {
            return Instance.Value == other.Instance.Value && Position == other.Position && Normal == other.Normal
                   && DistanceStuds.Equals(other.DistanceStuds) && IsTerrain == other.IsTerrain
                   && HasSurface == other.HasSurface;
        }

        public override bool Equals(object obj)
        {
            return obj is RbxWorldPick other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Instance.Value.GetHashCode() ^ (IsTerrain ? 7 : 11);
        }
    }
}
