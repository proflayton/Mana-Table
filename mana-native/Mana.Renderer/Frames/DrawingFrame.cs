using Microsoft.Xna.Framework.Graphics;

namespace Mana.Renderer;

/// <summary>A completed, ordered composition. Its command collection is detached from the next frame.</summary>
public sealed class DrawingFrame
{
    public IReadOnlyList<DrawCommand> Commands { get; }
    public IReadOnlyList<Texture2D> TextureResources { get; }
    public float TextScale { get; }
    public float Scale { get; }
    internal DrawingFrame(IEnumerable<DrawCommand> commands, float textScale, float scale)
    {
        Commands = Array.AsReadOnly(commands.ToArray());
        TextureResources = Array.AsReadOnly(Commands.Select(command => command.TextureResource).OfType<Texture2D>()
            .Distinct<Texture2D>(ReferenceEqualityComparer.Instance).ToArray());
        TextScale = textScale;
        Scale = scale;
    }
}
