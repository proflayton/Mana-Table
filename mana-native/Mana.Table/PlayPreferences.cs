using Mana.Magic;
using System.Text.Json;
using Mana.Renderer;

namespace Mana.Table;

internal sealed record PlayPreferences
{
    public bool Automatic { get; init; } = true;
    public bool ReducedMotion { get; init; }
    public bool Sounds { get; init; } = true;
    public string PlayerName { get; init; } = "";
    public bool MuteTableChat { get; init; }
    public HashSet<string> Stops { get; init; } = [];
    public static PlayPreferences Load(string profile)
    {
        try {
            var prefs = JsonSerializer.Deserialize<PlayPreferences>(File.ReadAllText(Path.Combine(profile, "play-preferences.json"))) ?? new();
            return prefs with { PlayerName = prefs.PlayerName ?? "", Stops = (prefs.Stops ?? []).Where(s => TurnGuide.Stops.Any(p => p.Key == s)).ToHashSet() };
        } catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }
    public void Save(string profile)
    {
        string path = Path.Combine(profile, "play-preferences.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
