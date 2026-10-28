using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table.Presentation;

// Screen-space translation shared by the battlefield and unfolded collections.
// Navigation never changes a card's world pose or grants a game action.
internal static class CameraKeyboardPan
{
    internal static Vector2 Offset(KeyboardState keys, TimeSpan elapsed)
    {
        if (keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl)
            || keys.IsKeyDown(Keys.LeftAlt) || keys.IsKeyDown(Keys.RightAlt)
            || keys.IsKeyDown(Keys.LeftWindows) || keys.IsKeyDown(Keys.RightWindows)) return Vector2.Zero;
        bool up = keys.IsKeyDown(Keys.W) || keys.IsKeyDown(Keys.Up);
        bool down = keys.IsKeyDown(Keys.S) || keys.IsKeyDown(Keys.Down);
        bool left = keys.IsKeyDown(Keys.A) || keys.IsKeyDown(Keys.Left);
        bool right = keys.IsKeyDown(Keys.D) || keys.IsKeyDown(Keys.Right);
        var direction = new Vector2((left ? 1 : 0) - (right ? 1 : 0), (up ? 1 : 0) - (down ? 1 : 0));
        if (direction == Vector2.Zero) return Vector2.Zero;
        direction.Normalize();
        float speed = keys.IsKeyDown(Keys.LeftShift) || keys.IsKeyDown(Keys.RightShift) ? 1200 : 600;
        // Bound catch-up after a stalled frame or losing window focus.
        return direction * speed * (float)Math.Clamp(elapsed.TotalSeconds, 0, .05);
    }
}
