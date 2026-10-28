using Mana.Audio;
using Microsoft.Xna.Framework.Audio;

namespace Mana.Renderer;

// Owns optional decoded assets. The mixer owns each returned playback voice;
// the window disposes this output after the mixer at graphics/audio teardown.
public sealed class MonoGameAudioOutput : IAudioOutput, IDisposable
{
    private readonly Dictionary<string, SoundEffect> sounds = new(StringComparer.Ordinal);
    private readonly HashSet<MonoGameAudioVoice> voices = [];
    private bool disposed;

    public MonoGameAudioOutput(string directory)
    {
        foreach (string key in new[] { "card-play", "your-priority", "combat", "life-loss", "game-end" }) {
            string path = Path.Combine(directory, key + ".wav");
            if (!File.Exists(path)) continue;
            try {
                using var file = File.OpenRead(path);
                sounds[key] = SoundEffect.FromStream(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                NotSupportedException or ArgumentException || Unavailable(ex)) { }
        }
    }

    public IAudioVoice? CreateVoice(string key)
    {
        if (disposed || !sounds.TryGetValue(key, out var sound)) return null;
        try {
            var voice = new MonoGameAudioVoice(sound.CreateInstance(), Release);
            voices.Add(voice);
            return voice;
        }
        catch (Exception ex) when (Unavailable(ex)) { return null; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var voice in voices.ToArray()) voice.Dispose();
        foreach (var sound in sounds.Values) {
            try { sound.Dispose(); }
            catch (Exception ex) when (Unavailable(ex)) { }
        }
        sounds.Clear();
    }

    private void Release(MonoGameAudioVoice voice) => voices.Remove(voice);

    private static bool Unavailable(Exception ex) =>
        ex is NoAudioHardwareException or InstancePlayLimitException or InvalidOperationException;

    private sealed class MonoGameAudioVoice(SoundEffectInstance instance, Action<MonoGameAudioVoice> release) : IAudioVoice
    {
        private bool disposed;
        private float volume = 1;

        public float Volume {
            get => volume;
            set {
                if (disposed) return;
                volume = float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
                try { instance.Volume = volume; }
                catch (Exception ex) when (Unavailable(ex)) { Dispose(); }
            }
        }

        public bool IsPlaying {
            get {
                if (disposed) return false;
                try { return instance.State == SoundState.Playing; }
                catch (Exception ex) when (Unavailable(ex)) { Dispose(); return false; }
            }
        }

        public bool Play()
        {
            if (disposed) return false;
            try { instance.Play(); return IsPlaying; }
            catch (Exception ex) when (Unavailable(ex)) { Dispose(); return false; }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try {
                try { instance.Stop(); }
                catch (Exception ex) when (Unavailable(ex)) { }
                try { instance.Dispose(); }
                catch (Exception ex) when (Unavailable(ex)) { }
            }
            finally { release(this); }
        }
    }
}
