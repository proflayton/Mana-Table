using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Forge;
using Mana.Magic;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Point = Microsoft.Xna.Framework.Point;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

internal static partial class HeadlessPresentationSmoke
{
    private static void CheckDepartedDefender(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        JsonElement Wire(bool? hasDefender, object? defender)
        {
            var attack = new Dictionary<string, object?> { ["cardId"] = "attacker", ["defendingPlayerId"] = 0,
                ["defender"] = defender, ["blockerIds"] = new[] { "blocker" }, ["eligibleBlockerIds"] = new[] { "blocker" }, ["blocked"] = true };
            if (hasDefender != null) attack["hasDefender"] = hasDefender;
            return JsonSerializer.SerializeToElement(new { id = "departed-defender-wire", status = "playing", viewerId = 0,
                turn = 4, phase = "Combat damage", phaseKey = "COMBAT_DAMAGE", activePlayerId = 1,
                players = new[] { new Player(0, "You", 40, false, false, ImmutableDictionary<string, int>.Empty, []),
                    new Player(1, "Other", 40, true, false, ImmutableDictionary<string, int>.Empty, []) }, stack = Array.Empty<object>(),
                combat = new { attackers = new[] { attack }, attackOptions = Array.Empty<object>() }, prompt = (object?)null });
        }
        var absent = ForgeSnapshotDecoder.Decode(Wire(false, null))!;
        check(absent.Combat?.Attackers is [{ HasDefender: false, Defender: null, DefendingPlayerId: 0,
                Blocked: true, BlockerIds: ["blocker"], EligibleBlockerIds: ["blocker"] }],
            "An explicit departed defender decodes without a helper identity while preserving the attacker, defending relation and blocked state");
        check(ForgeSnapshotDecoder.Decode(Wire(null, null))!.Combat!.Attackers[0].HasDefender,
            "An older combat snapshot omitting defender metadata retains its legacy player assignment");
        foreach (var invalid in new[] { Wire(false, new { kind = "player", id = "0", playerId = 0, name = "You" }),
            Wire(true, new { kind = "card", id = (string?)null, playerId = 0, name = "Invalid live identity" }) }) {
            var scope = new ForgeCommandScope(); scope.Observe(absent);
            bool rejected = false;
            try { scope.ObserveRaw(invalid); } catch (IOException) { rejected = true; }
            check(rejected && scope.ObservedGameId.Length == 0,
                "Contradictory absence and malformed live card identities still revoke the entire native command scope");
        }

        Card Body(string id, string name) => new() { VisualId = id, CombatId = id, Key = id + "-key", Name = name,
            Type = "Creature", Power = 3, Toughness = 3 };
        var blocker = Body("blocker", "Surviving blocker") with { Blocking = true };
        var attacker = Body("attacker", "Surviving attacker") with { Attacking = true };
        var walker = new Card { VisualId = "walker", CombatId = "walker", Name = "Defending permanent", Type = "Planeswalker",
            Counters = ImmutableDictionary<string, int>.Empty.Add("LOYALTY", 1) };
        var before = Example() with { Id = "departed-defender-presentation", ActivePlayerId = 1, Decision = null,
            Combat = new([new("attacker", 0, ["blocker"], ["blocker"]) { Defender = new("card", "walker", 0, walker.Name), Blocked = true }], [], null),
            Players = Example().Players.Select(player => player with { Zones = player.Zones.Select(zone => zone.Name == "Battlefield"
                ? zone with { Count = player.Id == 0 ? 2 : player.Id == 1 ? 1 : 0,
                    Cards = player.Id == 0 ? [blocker, walker] : player.Id == 1 ? [attacker] : [] } : zone).ToImmutableArray() }).ToImmutableArray() };
        using var run = new Runner(profile, "departed-defender-combat", before, trace, performance);
        run.Tick("defender-present"); run.Settle("defender-present");
        int beforeArrows = run.Frame.Drawing.Commands.OfType<DrawCommand.Arrow>().Count();
        var after = before with { Combat = before.Combat! with { Attackers = [before.Combat.Attackers[0] with { HasDefender = false, Defender = null }] },
            Players = before.Players.Select(player => player.Id != 0 ? player : player with { Zones = player.Zones.Select(zone => zone.Name switch {
                "Battlefield" => zone with { Count = 1, Cards = [blocker] },
                "Graveyard" => zone with { Count = 1, Cards = [walker with { Counters = ImmutableDictionary<string, int>.Empty }] },
                _ => zone }).ToImmutableArray() }).ToImmutableArray() };
        check(PresentationPacing.ShouldPresent(after with { Combat = after.Combat with {
                Attackers = [after.Combat.Attackers[0] with { HasDefender = true }] } }, after),
            "Explicit defender departure is a presentation boundary even when the older optional defender face was omitted");
        run.Present(after); run.Settle("defender-left");
        check(beforeArrows == 2 && run.Frame.Drawing.Commands.OfType<DrawCommand.Arrow>().Count() == 1
            && run.Frame.Cards.Any(card => card.Id == "attacker") && run.Frame.Cards.Any(card => card.Id == "blocker")
            && !Text(run.Frame).Any(value => value.Contains("ATTACKING YOU")),
            "A defending permanent leaving removes its attack destination without redirecting to its controller or removing the surviving block connection");
        run.Input(new Point(20, 60), keys: new KeyboardState(Keys.C));
        run.Input(new Point(20, 60)); run.Settle("review-departed-defender");
        check(Text(run.Frame).Contains("DEFENDER LEFT COMBAT") && Text(run.Frame).Any(value => value.Contains("Blocked by Surviving blocker"))
            && run.Engine.Replies.Count == 0,
            "Combat review describes the departed defender and surviving blocker without a gameplay command or disconnection");

        var declaration = after with { Decision = new("current-attacks", new(), new AttackPayload(new("Done", true))),
            Combat = after.Combat! with { AttackOptions = [new("attacker", [new("player", "0", 0, "You")])] } };
        check(CombatGuide.Remove(declaration, "attacker") == null,
            "An absent defender cannot synthesize a player-toggle recall even when that player is a current legal alternative");
    }
}
