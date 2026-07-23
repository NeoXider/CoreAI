namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// Part shape in Roblox terms. Values mirror Enum.PartType so the Lua layer maps enum
    /// items by value; the binder alone decides how each shape materializes in Unity.
    /// </summary>
    public enum RbxPartShape
    {
        Ball = 0,
        Block = 1,
        Cylinder = 2,
        Wedge = 3,
        CornerWedge = 4
    }
}
