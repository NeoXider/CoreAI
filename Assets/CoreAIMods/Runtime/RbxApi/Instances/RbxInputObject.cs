using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Roblox InputObject payload for UserInputService events: KeyCode + UserInputType +
    /// UserInputState as interned enum items, Position/Delta as Roblox Vector3 (Z always 0 for
    /// screen-space input). Immutable — one object describes one input event.
    /// WHY: Roblox models InputObject as an Instance subclass, but MVP1 ships it as a plain
    /// read-only value: the mini-game corpus only reads the four properties, and keeping it out of
    /// the instance registry avoids ledger/ownership noise for per-frame throwaway objects.
    /// </summary>
    public sealed class RbxInputObject
    {
        public RbxInputObject(RbxEnumItem keyCode, RbxEnumItem userInputType,
            RbxEnumItem userInputState, RbxVector3 position, RbxVector3 delta)
        {
            KeyCode = keyCode;
            UserInputType = userInputType;
            UserInputState = userInputState;
            Position = position;
            Delta = delta;
        }

        /// <summary>Enum.KeyCode item; Enum.KeyCode.Unknown for non-key input.</summary>
        public RbxEnumItem KeyCode { get; }

        /// <summary>Enum.UserInputType item (Keyboard/MouseButton1/MouseMovement/Gamepad1/...).</summary>
        public RbxEnumItem UserInputType { get; }

        /// <summary>Enum.UserInputState item (Begin/Change/End).</summary>
        public RbxEnumItem UserInputState { get; }

        /// <summary>Screen position in pixels for pointer input; Vector3.zero for keys.</summary>
        public RbxVector3 Position { get; }

        /// <summary>Position change since the previous event (MouseMovement); otherwise zero.</summary>
        public RbxVector3 Delta { get; }

        /// <summary>Roblox tostring format for InputObjects.</summary>
        public override string ToString() => "InputObject";
    }
}
