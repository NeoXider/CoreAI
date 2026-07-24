using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;

namespace CoreAI.Mods.Rbx.Instances
{
    /// <summary>
    /// Engine-free input poll seam behind <see cref="RbxUserInputService"/> (mirrors the
    /// <c>IRbxCameraRig</c> pattern): implementations report the CURRENT device state each
    /// frame and the service derives this-frame began/ended by diffing successive polls, so a
    /// backend never has to track edge state. Key identity is the Roblox <c>Enum.KeyCode</c>
    /// VALUE (letters 97..122, gamepad buttons 1000+), which keeps the contract engine-neutral.
    /// The mouse location uses Roblox screen convention: pixels, origin at the top-left.
    /// </summary>
    // TODO: com.unity.inputsystem may be removed — keep UserInputService behind IInputSource so
    // the New-Input-System backend can be swapped without touching the Lua-facing API.
    public interface IInputSource
    {
        /// <summary>Adds the Roblox KeyCode value of every currently held key/gamepad button.</summary>
        void CollectPressedKeyCodes(ICollection<int> buffer);

        /// <summary>True while the key/gamepad button with the Roblox KeyCode value is held.</summary>
        bool IsKeyCodeDown(int keyCodeValue);

        /// <summary>True while the mouse button is held; 0 = left, 1 = right, 2 = middle.</summary>
        bool IsMouseButtonDown(int button);

        /// <summary>Mouse position in pixels, top-left origin (Roblox screen convention).</summary>
        RbxVector2 GetMouseLocation();
    }
}
