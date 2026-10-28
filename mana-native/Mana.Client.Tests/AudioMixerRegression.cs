using Mana.Audio;
using System.Text.Json;

internal static class AudioMixerRegression
{
    internal static void Run(Action<bool, string> check)
    {
        var output = new Output();
        var mixer = new AudioMixer(output);
        var effect = new AudioCue("card-play", AudioBus.Effects, .4f);
        check(mixer.Play(effect) && Near(output.Voices[0].Volume, .4f),
            "Default audio preserves a cue's authored relative level through the output boundary");
        var activeEffect = output.Voices[0];
        mixer.SetMix(new() { Master = new(.5f), Effects = new(.4f), Notifications = new(.8f), Music = new(.6f) });
        check(Near(activeEffect.Volume, .08f) && activeEffect.Plays == 1 && activeEffect.Disposals == 0,
            "Changing master and Effects levels immediately adjusts an already playing cue without restarting it");
        check(mixer.Play(new("your-priority", AudioBus.Notifications, .25f)) && Near(output.Voices[1].Volume, .1f),
            "Notification cues use their own bus below the same master level");
        var notification = output.Voices[1];
        mixer.SetMix(mixer.Mix with { Effects = mixer.Mix.Effects with { Muted = true } });
        check(activeEffect.Volume == 0 && Near(notification.Volume, .1f),
            "Muting Effects silences its current voice without changing an active notification");
        int beforeMuted = output.Requests;
        check(!mixer.Play(effect) && output.Requests == beforeMuted,
            "A muted bus never allocates a new one-shot voice");
        mixer.SetMix(mixer.Mix with { Master = mixer.Mix.Master with { Muted = true } });
        check(activeEffect.Volume == 0 && notification.Volume == 0 && mixer.Mix.Master.Volume == .5f
                && mixer.Mix.Effects.Volume == .4f && mixer.Mix.Notifications.Volume == .8f,
            "Master mute immediately silences every active bus while retaining all chosen levels");
        check(!mixer.Play(new("your-priority", AudioBus.Notifications)) && output.Requests == beforeMuted,
            "Master mute admits no new notification even when that bus is enabled");
        mixer.SetMix(mixer.Mix with { Master = mixer.Mix.Master with { Muted = false } });
        check(activeEffect.Volume == 0 && Near(notification.Volume, .1f),
            "Unmuting master restores current notification volume while retaining the independent Effects mute");
        mixer.SetMix(mixer.Mix with { Effects = mixer.Mix.Effects with { Muted = false } });
        check(Near(activeEffect.Volume, .08f) && activeEffect.Plays == 1,
            "Unmuting a bus restores the current voice's chosen gain without a second playback");

        notification.IsPlaying = false;
        mixer.Update();
        check(notification.Disposals == 1 && activeEffect.Disposals == 0,
            "The mixer reaps completed voices and keeps other playing voices alive");
        mixer.Update();
        check(notification.Disposals == 1, "A completed voice is disposed once across repeated audio updates");
        mixer.SetMix(mixer.Mix with { Master = new(0) });
        check(activeEffect.Volume == 0 && !mixer.Play(effect) && output.Requests == beforeMuted,
            "Master zero adjusts existing audio and suppresses new one-shot playback without allocating resources");
        mixer.SetMix(mixer.Mix with { Master = new(1) });
        check(Near(activeEffect.Volume, .16f), "Raising master from zero affects the remaining active voice immediately");
        check(mixer.Play(new("future-music", AudioBus.Music)) && Near(output.Voices[^1].Volume, .6f),
            "A future Music cue uses the same mixer and independent bus without a second playback system");

        var normalized = new AudioMix {
            Master = new(float.NaN), Effects = new(-4), Notifications = new(7), Music = new(float.PositiveInfinity, true)
        }.Normalize();
        check(normalized.Master.Volume == 1 && normalized.Effects.Volume == 0 && normalized.Notifications.Volume == 1
                && normalized.Music.Volume == 1 && normalized.Music.Muted,
            "Nonfinite persisted gains use their default and finite gains clamp to the supported range without losing mute");
        mixer.SetMix(normalized);
        check(activeEffect.Volume == 0 && output.Voices[^1].Volume == 0,
            "Normalized settings are also the settings applied to already playing voices");
        mixer.SetMix(new() { Master = new(.3f), Notifications = new(.5f) });
        check(mixer.Play(new("priority-invalid-trim", AudioBus.Notifications, float.NaN))
                && Near(output.Voices[^1].Volume, .15f),
            "An invalid authored gain cannot pass NaN or an unbounded level to an audio device");
        int beforeRejected = output.Requests;
        check(!mixer.Play(new("unknown-route", (AudioBus)999)) && !mixer.Play(new(" ", AudioBus.Effects))
                && !mixer.Play(new("zero-trim", AudioBus.Effects, -1)) && output.Requests == beforeRejected,
            "Unknown routes, absent cue keys and zero authored gain cannot allocate audio voices");
        var missing = JsonSerializer.Deserialize<AudioMix>("""{"Master":null,"Effects":null,"Notifications":null,"Music":null}""")!.Normalize();
        check(Enum.GetValues<AudioBus>().All(bus => missing.Gain(bus) == 1),
            "Missing persisted mixer levels recover neutral defaults before playback");
        var retained = mixer.Mix with { Notifications = new(.37f, true) };
        check(JsonSerializer.Deserialize<AudioMix>(JsonSerializer.Serialize(retained)) == retained,
            "The immutable mixer snapshot round trips independent bus volumes and mutes without device state");

        output.Unavailable = true;
        check(!mixer.Play(effect), "A missing output voice is a silent unsuccessful cue rather than an active playback");
        output.Unavailable = false; output.AcceptPlayback = false;
        check(!mixer.Play(effect) && output.Voices[^1] is { Plays: 1, Disposals: 1, IsPlaying: false },
            "A device or capacity rejection disposes its unsuccessful voice immediately");
        var rejected = output.Voices[^1];
        mixer.Update();
        check(rejected.Disposals == 1, "Rejected voices never enter the mixer's active ownership list");
        output.AcceptPlayback = true; output.ThrowOnPlay = true;
        bool failed = false;
        try { mixer.Play(effect); } catch (InvalidOperationException) { failed = true; }
        check(failed && output.Voices[^1].Disposals == 1,
            "An adapter failure during startup releases the voice before propagating the programming error");
        output.ThrowOnPlay = false;
        mixer.Dispose(); mixer.Dispose();
        check(output.Voices.All(voice => voice.Disposals == 1),
            "Mixer disposal releases every remaining voice exactly once, including active playback");
        bool rejectedAfterDispose = false;
        try { mixer.Play(effect); } catch (ObjectDisposedException) { rejectedAfterDispose = true; }
        check(rejectedAfterDispose, "A retired mixer cannot create playback after its device owner exits");
    }

    private static bool Near(float actual, float expected) => Math.Abs(actual - expected) < .00001f;

    private sealed class Output : IAudioOutput
    {
        internal List<Voice> Voices { get; } = [];
        internal int Requests { get; private set; }
        internal bool Unavailable { get; set; }
        internal bool AcceptPlayback { get; set; } = true;
        internal bool ThrowOnPlay { get; set; }
        public IAudioVoice? CreateVoice(string key)
        {
            Requests++;
            if (Unavailable) return null;
            var voice = new Voice(AcceptPlayback, ThrowOnPlay); Voices.Add(voice); return voice;
        }
    }

    private sealed class Voice(bool acceptPlayback, bool throwOnPlay) : IAudioVoice
    {
        public float Volume { get; set; }
        public bool IsPlaying { get; set; }
        internal int Plays { get; private set; }
        internal int Disposals { get; private set; }
        public bool Play()
        {
            Plays++;
            if (throwOnPlay) throw new InvalidOperationException("Controlled adapter startup failure.");
            IsPlaying = acceptPlayback; return acceptPlayback;
        }
        public void Dispose() { Disposals++; IsPlaying = false; }
    }
}
