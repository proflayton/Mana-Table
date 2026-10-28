namespace Mana.Audio;

/// <summary>A device adapter creates independently adjustable playback voices, or returns null when unavailable.</summary>
public interface IAudioOutput
{
    IAudioVoice? CreateVoice(string key);
}

/// <summary>Disposal stops playback and releases its device resources. Play reports capacity/device failure.</summary>
public interface IAudioVoice : IDisposable
{
    float Volume { get; set; }
    bool IsPlaying { get; }
    bool Play();
}

/// <summary>Owns active one-shot voices. Settings changes affect existing playback as well as future cues.</summary>
public sealed class AudioMixer(IAudioOutput output) : IDisposable
{
    private readonly IAudioOutput output = output ?? throw new ArgumentNullException(nameof(output));
    private readonly List<(AudioCue Cue, IAudioVoice Voice)> voices = [];
    private bool disposed;
    public AudioMix Mix { get; private set; } = new();

    public void SetMix(AudioMix mix)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(mix);
        Mix = mix.Normalize();
        Update();
        foreach (var (cue, voice) in voices) voice.Volume = Gain(cue);
    }

    public bool Play(AudioCue cue)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Update();
        float gain = Gain(cue);
        if (string.IsNullOrWhiteSpace(cue.Key) || gain <= 0) return false;
        var voice = output.CreateVoice(cue.Key);
        if (voice == null) return false;
        bool owned = false;
        try {
            voice.Volume = gain;
            if (!voice.Play()) return false;
            voices.Add((cue, voice)); owned = true;
            return true;
        } finally {
            if (!owned) voice.Dispose();
        }
    }

    public void Update()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        for (int i = voices.Count - 1; i >= 0; i--) {
            if (voices[i].Voice.IsPlaying) continue;
            voices[i].Voice.Dispose(); voices.RemoveAt(i);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var (_, voice) in voices) voice.Dispose();
        voices.Clear();
    }

    private float Gain(AudioCue cue) => Mix.Gain(cue.Bus) * AudioLevel.NormalizeVolume(cue.Volume);
}
