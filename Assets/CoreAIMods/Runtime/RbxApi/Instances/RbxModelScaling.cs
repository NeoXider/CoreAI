using System;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// The geometry of <c>Model:ScaleTo</c>: a part's size and its offset from the model's pivot at scale 1,
    /// and where they go at another scale. Engine-free; the Lua binding writes the results through the part
    /// sink and keeps the scale-1 values on the <see cref="RbxModel"/>.
    /// </summary>
    /// <remarks>
    /// WHY the scale-1 values are kept: the mirror says <c>ScaleTo(x)</c> followed by <c>ScaleTo(1)</c> gives back
    /// exactly the original model, with no floating point drift, which is why the call is ScaleTo and not
    /// ScaleBy; every call therefore scales the remembered scale-1 geometry, never the current one.
    /// </remarks>
    public static class RbxModelScaling
    {
        /// <summary>
        /// True for a scale factor <c>Model:ScaleTo</c> accepts: a finite number greater than zero.
        /// </summary>
        public static bool IsValidFactor(double factor)
        {
            return factor > 0d && !double.IsInfinity(factor) && !double.IsNaN(factor);
        }

        /// <summary>
        /// The size and pivot-relative offset a part at <paramref name="cframe"/> and <paramref name="size"/>
        /// had at scale 1, read while the model's scale is <paramref name="currentScale"/>.
        /// </summary>
        public static void OriginAtScaleOne(in RbxCFrame pivot, in RbxCFrame cframe, RbxVector3 size,
            double currentScale, out RbxVector3 sizeAtOne, out RbxCFrame offsetAtOne)
        {
            float inverse = (float)(1d / currentScale);
            RbxCFrame offset = pivot.Inverse() * cframe;
            sizeAtOne = size * inverse;
            offsetAtOne = offset.Rotation + offset.Position * inverse;
        }

        /// <summary>
        /// Where a part with scale-1 size <paramref name="sizeAtOne"/> and offset <paramref name="offsetAtOne"/>
        /// goes at scale <paramref name="factor"/> around <paramref name="pivot"/>: its size and its offset's
        /// position grow by the factor; its orientation does not change.
        /// </summary>
        public static void Scaled(in RbxCFrame pivot, RbxVector3 sizeAtOne, in RbxCFrame offsetAtOne, double factor,
            out RbxVector3 size, out RbxCFrame cframe)
        {
            if (!IsValidFactor(factor))
            {
                throw new ArgumentOutOfRangeException(nameof(factor), factor, "a scale factor is finite and above 0");
            }

            float scale = (float)factor;
            size = sizeAtOne * scale;
            cframe = pivot * (offsetAtOne.Rotation + offsetAtOne.Position * scale);
        }
    }
}
