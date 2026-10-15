using System.Text.Json;

namespace Mana.Table;

internal sealed record ExperiencePreferences
{
    public bool WelcomeSeen { get; init; }
    public bool TableGuideSeen { get; init; }
    public HashSet<string> LearnedControls { get; init; } = [];

    public static ExperiencePreferences Load(string profile)
    {
        string path = Path.Combine(profile, "experience.json");
        if (File.Exists(path)) {
            try {
                var saved = JsonSerializer.Deserialize<ExperiencePreferences>(File.ReadAllText(path)) ?? new();
                return saved with { LearnedControls = saved.LearnedControls ?? [] };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
        }
        // Check before engine initialization, which creates the decks directory
        // even for a new player. Existing players can open the guide from Help.
        bool existing = File.Exists(Path.Combine(profile, "play-preferences.json"));
        try {
            string decks = Path.Combine(profile, "decks");
            existing |= Directory.Exists(decks) && Directory.EnumerateFiles(decks, "*", SearchOption.AllDirectories).Any();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new() { WelcomeSeen = existing, TableGuideSeen = existing };
    }

    public void Save(string profile)
    {
        string path = Path.Combine(profile, "experience.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
