using System;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// "A text field of the runtime UI has keyboard focus." A character or camera controller reads this
    /// (or listens to the event) to stop treating keys as movement while the player types. The runtime
    /// UI host implements it; an input-focus port elsewhere can adapt it without this assembly knowing
    /// that port's type.
    /// </summary>
    public interface IRuntimeUiTextInputFocus
    {
        /// <summary>True while a text field inside a runtime UI screen holds keyboard focus.</summary>
        bool IsTextInputFocused { get; }

        /// <summary>Raised with the new value whenever <see cref="IsTextInputFocused"/> changes.</summary>
        event Action<bool> TextInputFocusChanged;
    }
}
