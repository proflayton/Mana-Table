using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;

namespace Mana.Renderer;

// Optional artist-authored assets. Missing art uses neutral geometric surfaces;
// missing sounds are silent. No generated illustration is shipped or downloaded.
public sealed class TableAssets : IDisposable
{
    public Texture2D? Arena { get; }
    public Texture2D? CardBack { get; }
    private readonly Dictionary<string, SoundEffect> sounds = [];
    public TableAssets(GraphicsDevice device, string directory)
    {
        Texture2D? Load(string filename) {
            string path = Path.Combine(directory, filename);
            if (!File.Exists(path)) return null;
            try { using var file = File.OpenRead(path); return Texture2D.FromStream(device, file); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { return null; }
        }
        Arena = Load("arena.png"); CardBack = Load("card-back.png");
        foreach (string name in new[] { "card-play", "your-priority", "combat", "life-loss", "game-end" }) {
            string path = Path.Combine(directory, name + ".wav");
            if (!File.Exists(path)) continue;
            try { using var file = File.OpenRead(path); sounds[name] = SoundEffect.FromStream(file); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NoAudioHardwareException) { }
        }
    }
    public void Play(string name, float volume) { if (sounds.TryGetValue(name, out var sound)) try { sound.Play(Math.Clamp(volume, 0, 1), 0, 0); } catch (NoAudioHardwareException) { } }
    public void Dispose() { Arena?.Dispose(); CardBack?.Dispose(); foreach (var sound in sounds.Values) sound.Dispose(); }
}
