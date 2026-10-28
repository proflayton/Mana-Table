using Mana.Audio;
using Microsoft.Xna.Framework.Graphics;

namespace Mana.Renderer;

// Optional artist-authored assets. Missing art uses neutral geometric surfaces;
// missing sounds are silent. No generated illustration is shipped or downloaded.
public sealed class TableAssets : IDisposable
{
    public Texture2D? Arena { get; }
    public Texture2D? CardBack { get; }
    public Texture2D? ManaSymbols { get; }
    public IAudioOutput AudioOutput => audioOutput;
    private readonly MonoGameAudioOutput audioOutput;
    public TableAssets(GraphicsDevice device, string directory)
    {
        Texture2D? Load(string filename) {
            string path = Path.Combine(directory, filename);
            if (!File.Exists(path)) return null;
            try { using var file = File.OpenRead(path); return Texture2D.FromStream(device, file); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { return null; }
        }
        Arena = Load("arena.png"); CardBack = Load("card-back.png"); ManaSymbols = Load("mana-symbols.png");
        audioOutput = new MonoGameAudioOutput(directory);
    }
    public void Dispose() { audioOutput.Dispose(); Arena?.Dispose(); CardBack?.Dispose(); ManaSymbols?.Dispose(); }
}
