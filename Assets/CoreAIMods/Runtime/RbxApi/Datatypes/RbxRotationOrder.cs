namespace CoreAI.Mods.Rbx.Datatypes
{
    /// <summary>
    /// Mirror of Enum.RotationOrder (values match the Roblox enum). Used by
    /// CFrame.fromEulerAngles; the marshaller maps Enum.RotationOrder items onto this.
    /// </summary>
    public enum RbxRotationOrder
    {
        XYZ = 0,
        XZY = 1,
        YZX = 2,
        YXZ = 3,
        ZXY = 4,
        ZYX = 5
    }
}
