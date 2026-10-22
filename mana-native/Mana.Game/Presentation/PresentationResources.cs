using Mana.Contracts;
using Microsoft.Xna.Framework.Graphics;

namespace Mana.Table.Presentation;

/// <summary>Optional device-owned resources supplied by the window. Composition also works with none.</summary>
internal sealed class PresentationResources
{
    internal Func<Card, Texture2D?> Card { get; set; } = _ => null;
    internal Func<Card[], ArtworkReadiness> Artwork { get; set; } = _ => default;
    internal Action<string, float> Play { get; set; } = (_, _) => { };
    internal Texture2D? Arena { get; set; }
    internal Texture2D? CardBack { get; set; }
    internal Texture2D? ManaSymbols { get; set; }
}
