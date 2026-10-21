using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;

internal static class PublicStateDescriptorChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var legacy = JsonSerializer.Deserialize<Card>("{\"type\":\"Creature\",\"combatKeywords\":[\"flying\",\"hexproof\"]}", options)!;
        check(legacy.States == null && CardPresentation.HasAbility(legacy, "flying")
            && CardPresentation.StateLabels(legacy).Contains("Hexproof"),
            "Older keyword-only engine payloads retain their existing card presentation");
        var authoritativeEmpty = JsonSerializer.Deserialize<Card>("{\"type\":\"Creature\",\"combatKeywords\":[\"flying\"],\"states\":[]}", options)!;
        check(!CardPresentation.HasAbility(authoritativeEmpty, "flying") && CardPresentation.AbilityLabels(authoritativeEmpty).Length == 0,
            "An empty typed state list authoritatively removes abilities instead of resurrecting a legacy keyword hint");
        PublicState Ability(string key, string title, int count = 1) => new("keyword:" + key + ":" + title,
            title, "ability", count, null, "Engine explanation for " + title, key);
        var qualified = legacy with { States = [Ability("ward", "Ward {2}", 2), Ability("hexproof", "Hexproof from red"),
            Ability("protection", "Protection from blue"), Ability("shroud", "Shroud"), Ability("future", "Unfamiliar mechanic — exact qualifier")] };
        check(CardPresentation.HasAbility(qualified, "shroud") && !CardPresentation.HasAbility(qualified, "flying")
            && CardPresentation.Abilities(qualified).Contains("future"),
            "Typed effective abilities use public keys without a native keyword allowlist or stale legacy abilities");
        check(CardPresentation.AbilityLabel(qualified, "ward") == "Ward {2} ×2"
            && CardPresentation.AbilityLabels(qualified).Contains("Hexproof from red")
            && CardPresentation.StateLabels(qualified).Contains("Protection from blue")
            && CardPresentation.AbilityDetails(qualified).Any(detail => detail.Contains("Unfamiliar mechanic — exact qualifier") && detail.Contains("Engine explanation")),
            "Qualified and unfamiliar effective abilities retain the engine's exact label, reminder, and nonredundant instance count");
        var publicMasked = qualified with { FaceDown = true, Type = "Creature", Name = "Face-down card", Text = "", ArtName = "" };
        check(CardPresentation.HasAbility(publicMasked, "ward") && CardPresentation.AbilityLabel(publicMasked, "hexproof") == "Hexproof from red"
            && CardPresentation.Artwork(publicMasked) == null,
            "Anonymous face-down battlefield characteristics remain readable while card artwork and printed identity stay concealed");
        var privateMasked = publicMasked with { Type = "" };
        check(CardPresentation.Abilities(privateMasked).Length == 0 && CardPresentation.AbilityLabels(privateMasked).Length == 0
            && CardPresentation.AbilityDetails(privateMasked).Length == 0 && CardPresentation.AbilityLabel(privateMasked, "ward") == "",
            "A hidden-zone face cannot expose typed ability labels, reminders, or qualifiers");
        var custom = new PublicState("future-table", "Future public progress", "progress", 6, 6,
            "A new engine mechanic with a different actual maximum.", "future");
        check(PublicStatePresentation.Caption(custom) == "Future public progress 6/6 MAX"
            && PublicStatePresentation.Caption(custom with { Kind = "future-kind", Value = 3, Maximum = null }) == "Future public progress 3"
            && CardPresentation.StateLabels(qualified with { States = [custom] }).Contains("Future public progress 6/6 MAX"),
            "Card and table state captions retain unfamiliar kinds, values, and declared maxima without mechanic-specific code");
        var state = new GameSnapshot("state-wire", "playing", null, null, 0, 1, "Main phase", "MAIN1", 0, [], [], null, null) {
            States = [new("day-night", "Night", "status", null, null, "It is night.", "day-night"), custom]
        };
        var restored = JsonSerializer.Deserialize<GameSnapshot>(JsonSerializer.Serialize(state), options)!;
        check(restored.States.SequenceEqual(state.States)
            && PublicStatePresentation.Description(restored.States[0]) == "Night: It is night.",
            "The Forge snapshot DTO preserves generic table descriptors and engine explanations through wire serialization");
    }
}
