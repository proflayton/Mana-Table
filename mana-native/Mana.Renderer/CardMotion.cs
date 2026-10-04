using Microsoft.Xna.Framework;

namespace Mana.Renderer;

public static class CardMotion
{
    // A resting airborne pose is shared by painting, hit testing and arrows.
    // Reduced motion retains the readable height without idle oscillation.
    public static Pose Airborne(Pose rest, double now, bool reducedMotion, string identity)
    {
        int phase = 0;
        foreach (char character in identity) phase = (phase * 31 + character) & 1023;
        float height = 16 + (reducedMotion ? 0 : MathF.Sin((float)now * 1.65f + phase * .01f) * 2.5f);
        return rest with { Center = rest.Center - new Vector2(0, height), Elevation = rest.Elevation + height };
    }
}
