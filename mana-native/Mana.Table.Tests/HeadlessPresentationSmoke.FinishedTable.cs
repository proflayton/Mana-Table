using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Client;
using Mana.Magic;

namespace Mana.Table;

internal static partial class HeadlessPresentationSmoke
{
    private static void CheckFinishedTable(string profile, Action<bool, string> check, List<object> trace, List<object> performance)
    {
        // Representative of the final-loser projection found in a real turn-38
        // game: public read-only faces can remain after their live identities
        // retire. No private deck, raw capture or invented action handle lives here.
        Card RetiredFace(int index) => JsonSerializer.Deserialize<Card>(JsonSerializer.Serialize(new {
            key = (string?)null, visualId = (string?)null, combatId = (string?)null,
            name = "Retained final face " + index, type = index % 2 == 0 ? "Creature" : "Land",
            text = "Public read-only final battlefield face.", selectable = false, highlighted = false,
            power = index % 2 == 0 ? (int?)2 : null, toughness = index % 2 == 0 ? (int?)2 : null
        }), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        foreach (int viewer in new[] { 0, 3 }) {
            var before = Example() with { ViewerId = viewer, Turn = 38, Decision = null, Combat = null, Stack = [] };
            var finished = before with {
                Revision = 100, BoardRevision = 100, Status = "finished", Result = viewer == 0 ? "Victory" : "Defeat",
                Decision = null, DecisionOwnerId = null, PriorityPlayerId = null, ActionClass = "",
                Players = before.Players.Select(player => player with {
                    Priority = false, Eliminated = player.Id != 0,
                    Zones = player.Zones.Select(zone => zone.Name == "Battlefield" && player.Id == 3
                        ? zone with { Count = 39, Cards = Enumerable.Range(0, 39).Select(RetiredFace).ToImmutableArray(), TopCard = null }
                        : zone.Name == "Hand" ? zone with { Count = 0, Cards = [], TopCard = null }
                        : zone with { Cards = zone.Cards.Select(card => card with { Key = "", Selectable = false, Highlighted = false }).ToImmutableArray() }
                    ).ToImmutableArray()
                }).ToImmutableArray()
            };
            // Keep this actual background-poll regression on the shipping delivery mode.
            using var run = new Runner(profile, "finished-retired-faces-" + viewer, before, trace, performance,
                ObservationDeliveryMode.Polled);
            run.Engine.ObserveOverride = () => Task.FromResult<GameSnapshot?>(finished);
            run.Until(() => run.View.Match?.Status == "finished", "terminal-observation");
            run.Settle("terminal-result");
            var text = Text(run.Frame);
            check(run.Engine.Observations > 0 && run.View.Overlay == "result"
                    && text.Contains(viewer == 0 ? "VICTORY" : "DEFEAT") && !text.Contains("GAME INTERRUPTED")
                    && run.View.Match!.Players.Single(player => player.Id == 3).Zone("Battlefield").Cards.Length == 39,
                "A completed turn-38 observation with 39 retired losing-seat faces renders the actual " + finished.Result + " in the foreground");
            var accepted = run.View.Match!;
            check(accepted.Players.Single(player => player.Id == 3).Zone("Battlefield").Cards.All(card =>
                    card.Key == "" && card.VisualId == "" && card.CombatId == "" && !card.Selectable && !card.Highlighted)
                    && !GameCommandPermissions.Allows(accepted, new PassPriority(accepted.Id, "retired"))
                    && !GameCommandPermissions.Allows(accepted, new SelectCard(accepted.Id, "retired", "retired"))
                    && run.Engine.Replies.Count == 0,
                "Legacy JSON-null terminal identities gain no synthetic identity, selection permission or game-command authority");

            finished = finished with { Revision = 101 };
            run.Until(() => run.View.Match?.Revision == 101, "later-terminal-poll");
            check(run.View.Overlay == "result" && Text(run.Frame).Contains(viewer == 0 ? "VICTORY" : "DEFEAT")
                    && run.Engine.Replies.Count == 0,
                "A subsequent terminal poll and presentation lifecycle update preserve the same perspective-correct result without commands");
            run.ClickHit("close-overlay"); run.Settle("inspect-final-table");
            var stable = run.ReadFrame("unchanged-final-table");
            check(run.View.Overlay == "" && run.Frame.Scene != null
                    && Fingerprint(stable.Drawing).SequenceEqual(Fingerprint(run.ReadFrame("same-terminal-frame").Drawing))
                    && run.Engine.Replies.Count == 0,
                "Inspecting and recomposing the retained final table remains stable after closing the result foreground");
        }
    }
}
