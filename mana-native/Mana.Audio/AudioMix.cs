using System.Text.Json.Serialization;

namespace Mana.Audio;

public enum AudioBus { Effects, Notifications, Music }

/// <summary>A retained level and an independent mute; unmuting restores the chosen volume.</summary>
public sealed record AudioLevel(float Volume = 1, bool Muted = false)
{
    public AudioLevel Normalize() => this with { Volume = NormalizeVolume(Volume) };
    [JsonIgnore] public float Gain => Muted ? 0 : NormalizeVolume(Volume);
    internal static float NormalizeVolume(float volume) => float.IsFinite(volume) ? Math.Clamp(volume, 0, 1) : 1;
}

/// <summary>One immutable gain tree shared by settings, cue admission and active playback.</summary>
public sealed record AudioMix
{
    public AudioLevel Master { get; init; } = new();
    public AudioLevel Effects { get; init; } = new();
    public AudioLevel Notifications { get; init; } = new();
    public AudioLevel Music { get; init; } = new();

    public AudioMix Normalize() => this with {
        Master = (Master ?? new()).Normalize(), Effects = (Effects ?? new()).Normalize(),
        Notifications = (Notifications ?? new()).Normalize(), Music = (Music ?? new()).Normalize()
    };

    public float Gain(AudioBus bus) => (Master ?? new()).Gain * ((bus switch {
        AudioBus.Effects => Effects, AudioBus.Notifications => Notifications, AudioBus.Music => Music, _ => null
    })?.Gain ?? 0);
}

/// <summary>The content key, mixer route and authored relative gain for one cue.</summary>
public readonly record struct AudioCue(string Key, AudioBus Bus, float Volume = 1);
