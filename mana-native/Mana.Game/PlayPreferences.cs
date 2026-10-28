using Mana.Magic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mana.Audio;
using Mana.Renderer;

namespace Mana.Table;

internal sealed record PlayPreferences
{
    public bool Automatic { get; init; } = true;
    public bool ReducedMotion { get; init; }
    private AudioMix audio = new();
    public AudioMix Audio { get => audio; init => audio = (value ?? new()).Normalize(); }
    // Source compatibility for fixtures; the mix owns the only persisted mute state.
    [JsonIgnore]
    public bool Sounds {
        get => !audio.Master.Muted;
        init => audio = audio with { Master = audio.Master with { Muted = !value } };
    }
    public float TextScale { get; init; } = 1;
    public string PlayerName { get; init; } = "";
    public bool MuteTableChat { get; init; }
    public HashSet<string> Stops { get; init; } = [];
    public static PlayPreferences Load(string profile)
    {
        try {
            string json = File.ReadAllText(Path.Combine(profile, "play-preferences.json"));
            var prefs = JsonSerializer.Deserialize<PlayPreferences>(json) ?? new();
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && !document.RootElement.TryGetProperty("Audio", out _)
                && document.RootElement.TryGetProperty("Sounds", out var legacy)
                && legacy.ValueKind is JsonValueKind.True or JsonValueKind.False)
                prefs = prefs with { Sounds = legacy.GetBoolean() };
            return prefs with { TextScale = float.IsFinite(prefs.TextScale) ? Math.Clamp(prefs.TextScale, 1, 1.3f) : 1,
                PlayerName = prefs.PlayerName ?? "", Stops = (prefs.Stops ?? []).Where(s => TurnGuide.Stops.Any(p => p.Key == s)).ToHashSet() };
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save(string profile)
    {
        string path = Path.Combine(profile, "play-preferences.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
