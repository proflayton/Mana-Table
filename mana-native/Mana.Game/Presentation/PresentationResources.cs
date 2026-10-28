using Mana.Contracts;
using Mana.Audio;
using Microsoft.Xna.Framework.Graphics;

namespace Mana.Table.Presentation;

/// <summary>Optional device-owned resources supplied by the window. Composition also works with none.</summary>
internal sealed class PresentationResources
{
    internal Func<Card, Texture2D?> Card { get; set; } = _ => null;
    internal Func<Card[], ArtworkReadiness> Artwork { get; set; } = _ => default;
    internal IAudioOutput AudioOutput { get; set; } = new SilentAudioOutput();
    internal Texture2D? Arena { get; set; }
    internal Texture2D? CardBack { get; set; }
    internal Texture2D? ManaSymbols { get; set; }

    private sealed class SilentAudioOutput : IAudioOutput
    {
        public IAudioVoice? CreateVoice(string key) => null;
    }
}
