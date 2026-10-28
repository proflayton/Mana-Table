using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table.Interaction;

// Global device samples are not input for this window until focus is owned.
// A focus-changing click/key cannot become a delayed in-game gesture.
internal sealed class ForegroundInput
{
    private bool awaitingRelease;
    internal bool Ready { get; private set; } = true;

    internal void Deactivate() { Ready = false; awaitingRelease = true; }

    internal bool Admit(bool active, MouseState mouse, KeyboardState keys)
    {
        if (!active) { Deactivate(); return false; }
        if (awaitingRelease) {
            bool neutral = mouse.LeftButton == ButtonState.Released && mouse.MiddleButton == ButtonState.Released
                && mouse.RightButton == ButtonState.Released && mouse.XButton1 == ButtonState.Released
                && mouse.XButton2 == ButtonState.Released && keys.GetPressedKeys().Length == 0;
            awaitingRelease = !neutral;
            // Consume this baseline sample, but allow new text events arriving
            // after neutral input rather than dropping their first character.
            Ready = neutral;
            return false;
        }
        Ready = true;
        return true;
    }
}
