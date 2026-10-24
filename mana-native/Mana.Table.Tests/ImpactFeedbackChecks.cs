using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Scenarios;

namespace Mana.Table;

internal static class ImpactFeedbackChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var baseline = CounterDamageScenario.Snapshot(1);
        var result = CounterDamageScenario.Snapshot(2);
        var feedback = new ImpactFeedback(); var feedbackActivity = new PublicActivityCursor();
        feedback.Observe(feedbackActivity.Observe(null, baseline), 0);
        feedback.Observe(feedbackActivity.Observe(baseline, result), 1);
        check(feedback.Cues is [{ Impact: { Kind: "damage", Amount: 3, CardId: CounterDamageScenario.TargetId } }],
            "Actual damage is anchored to the explicit recipient, independently of the resolving source and legacy CardId semantics");
        feedback.Observe(feedbackActivity.Observe(result, result), 1.5);
        check(feedback.Cues.Count == 1 && feedback.Cues[0].Started == 1, "Polling a consumed damage event neither repeats nor prolongs its impact");
        var death = result with { Players = result.Players.Select(p => p.Id != 1 ? p : p with { Zones = p.Zones.Select(z =>
            z.Name == "Battlefield" ? z with { Cards = z.Cards.Where(c => c.VisualId != CounterDamageScenario.TargetId).ToImmutableArray() }
            : z.Name == "Graveyard" ? z with { Count = 1, Cards = [result.Players[1].Zone("Battlefield").Cards[0]] } : z).ToImmutableArray() }).ToImmutableArray() };
        feedback.Observe(feedbackActivity.Observe(result, death), 1.6);
        check(feedback.Cues.Count == 1, "An actual damaged recipient remains identifiable through a public graveyard departure between snapshots");
        var nextBurst = death with { Activity = [.. death.Activity,
            new(10, 8, "MAIN1", "damage", 1, "Another actual damage event", CounterDamageScenario.TargetId, "Colossal Dreadmaw") {
                Impact = new("damage", 3, CounterDamageScenario.TargetId, "Colossal Dreadmaw")
            }] };
        feedback.Observe(feedbackActivity.Observe(death, nextBurst), 1.65);
        check(feedback.Cues is [{ ActivityId: 10, Started: 1.65 }],
            "A later observed burst replaces the recipient's earlier transient amount instead of leaving two indistinguishable damage receipts around its departure");
        var hidden = death with { Players = death.Players.Select(p => p.Id != 1 ? p : p with { Zones = p.Zones.Select(z =>
            z.Name == "Graveyard" ? z with { Cards = [], Count = 0 } : z.Name == "Hand" ? z with { Count = z.Count + 1 } : z).ToImmutableArray() }).ToImmutableArray() };
        feedback.Observe(feedbackActivity.Observe(death, hidden), 1.7);
        check(feedback.Cues.Count == 0, "A damage cue immediately drops a recipient whose occurrence is no longer public; it never preserves a hidden face");

        var returned = result with { Players = result.Players.Select(p => p.Id != 1 ? p : p with { Zones = p.Zones.Select(z =>
            z.Name != "Battlefield" ? z : z with { Cards = z.Cards.Select(card => card.VisualId != CounterDamageScenario.TargetId
                ? card : card with { Damage = 0 }).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray(),
            Activity = [.. result.Activity,
                new(20, 8, "MAIN1", "moved", 1, "Localized departure", CounterDamageScenario.TargetId, "Colossal Dreadmaw"),
                new(21, 8, "MAIN1", "arrived", 1, "Localized arrival", CounterDamageScenario.TargetId, "Colossal Dreadmaw")]
        };
        feedback.Observe(feedbackActivity.Observe(null, baseline), 0); feedback.Observe(feedbackActivity.Observe(baseline, returned), 1);
        check(feedback.Cues.Count == 0 && !ImpactFeedback.RecipientVisible(returned,
                result.Activity.Single(entry => entry.Impact is { Kind: "damage" }).Impact!,
                result.Activity.Single(entry => entry.Impact is { Kind: "damage" }).Id),
            "A damage event followed by an explicit exile/return arrival in one observation never anchors old damage to the returned permanent despite its unchanged public visual ID");
        feedback.Observe(feedbackActivity.Observe(null, baseline), 0); feedback.Observe(feedbackActivity.Observe(baseline, result), 1); feedback.Observe(feedbackActivity.Observe(result, returned), 1.2);
        check(feedback.Cues.Count == 0,
            "A later explicit arrival immediately invalidates an already displayed damage cue on the previous battlefield occurrence");
        var freshHit = returned with { Activity = [.. returned.Activity,
            new(22, 8, "MAIN1", "damage", 1, "Fresh damage after return", CounterDamageScenario.TargetId, "Colossal Dreadmaw") {
                Impact = new("damage", 1, CounterDamageScenario.TargetId, "Colossal Dreadmaw", DamageType: "normal")
            }] };
        feedback.Observe(feedbackActivity.Observe(returned, freshHit), 1.3);
        check(feedback.Cues is [{ ActivityId: 22, Impact: { Amount: 1, CardId: CounterDamageScenario.TargetId } }],
            "A new damage event ordered after the return remains visible; a public ID's historical arrival does not suppress later hits");
        feedback.Observe(feedbackActivity.Observe(null, baseline), 0); feedback.Observe(feedbackActivity.Observe(baseline, freshHit), 1.4);
        check(feedback.Cues is [{ ActivityId: 22, Impact: { Amount: 1 } }],
            "When damage, exile/return and fresh damage arrive in one observation only the fresh occurrence's hit is represented");

        var beforeGap = result with { Players = result.Players.Select(player => player.Id == 1 ? player with { Life = 38 } : player).ToImmutableArray(),
            Activity = [.. result.Activity,
                new(4, 8, "MAIN1", "life", 1, "Previous public life change", null, null) {
                    Impact = new("life", -2, PlayerId: 1, Before: 40, After: 38)
                }] };
        string otherRecipient = result.Viewer!.Zone("Battlefield").Cards.First(card => card.Type.Contains("Creature", StringComparison.Ordinal)).VisualId;
        var afterGap = beforeGap with { Players = beforeGap.Players.Select(player => player with {
            Life = player.Id == 2 ? 39 : player.Life,
            Zones = player.Zones.Select(zone => zone with { Cards = zone.Cards.Select(card => card.VisualId == otherRecipient ? card with { Damage = 1 } : card).ToImmutableArray() }).ToImmutableArray()
        }).ToImmutableArray(), Activity = [
            new(100, 8, "MAIN1", "damage", 0, "Fresh typed hit after truncated history", otherRecipient, "Another creature") {
                Impact = new("damage", 1, otherRecipient, "Another creature", DamageType: "normal") },
            new(101, 8, "MAIN1", "life", 2, "Fresh typed life change", null, null) {
                Impact = new("life", -1, PlayerId: 2, Before: 40, After: 39) }
        ] };
        feedback.Observe(feedbackActivity.Observe(null, baseline), 0); feedback.Observe(feedbackActivity.Observe(baseline, beforeGap), 1); feedback.Observe(feedbackActivity.Observe(beforeGap, afterGap), 1.2);
        check(feedback.Cues.Count == 3 && feedback.Cues.All(cue => cue.Impact.CardId != CounterDamageScenario.TargetId)
            && feedback.Cues.Any(cue => cue is { ActivityId: 4, Impact: { Kind: "life", Amount: -2, PlayerId: 1 } })
            && feedback.Cues.Any(cue => cue.ActivityId == 100 && cue.Impact is { Kind: "damage", Amount: 1 } && cue.Impact.CardId == otherRecipient)
            && feedback.Cues.Any(cue => cue is { ActivityId: 101, Impact: { Kind: "life", Amount: -1, PlayerId: 2 } }),
            "A nonoverlapping history gap conservatively clears old card anchors that may have missed a public reentry, while retaining prior player life and accepting fresh typed damage and life facts");

        var prevented = baseline with { Players = baseline.Players.Select(p => p.Id == 1 ? p with { Life = 38 } : p).ToImmutableArray(), Stack = [], Activity = [
            new(10, 8, "MAIN1", "damage", 1, "Localized prose is not parsed", CounterDamageScenario.SourceId, "All Will Be One") {
                Impact = new("damage", 2, PlayerId: 1, SourceCardId: CounterDamageScenario.SourceId, SourceName: "All Will Be One", Combat: false, Infect: false) },
            new(11, 8, "MAIN1", "life", 1, "Life changed", null, null) { Impact = new("life", -2, PlayerId: 1, Before: 40, After: 38) }
        ] };
        feedback.Observe(feedbackActivity.Observe(null, baseline), 0); feedback.Observe(feedbackActivity.Observe(baseline, prevented), 2);
        check(feedback.Cues.Count == 2 && feedback.Cues.Any(c => c.Impact is { Kind: "damage", Amount: 2, CardId: null, PlayerId: 1 })
            && feedback.Cues.Any(c => c.Impact is { Kind: "life", Amount: -2, Before: 40, After: 38 }),
            "Pending base three becomes reported actual two; player damage and signed life change remain separate facts without duplicate life feedback");
        var oldHost = prevented with { Activity = prevented.Activity.Select(e => e with { Impact = null }).ToImmutableArray() };
        feedback.Observe(feedbackActivity.Observe(null, baseline), 0); feedback.Observe(feedbackActivity.Observe(baseline, oldHost), 3);
        check(feedback.Cues is [{ Impact: { Kind: "life", Amount: -2, SourceCardId: null } }],
            "Older engines retain an observed life transition but never invent actual damage from old prose, pending values or life loss");
        feedback.Observe(feedbackActivity.Observe(oldHost, oldHost), 6);
        check(feedback.Cues.Count == 0, "Result feedback expires without replaying retained history");
        feedback.Observe(feedbackActivity.Observe(null, prevented), 7);
        check(feedback.Cues.Count == 0, "Joining an existing table treats public activity as a baseline rather than replaying its damage");
        var effects = new SceneEffects(); var effectsActivity = new PublicActivityCursor(); effects.Observe(effectsActivity.Observe(baseline, result), 1);
        check(effects.Cards.All(c => c.Kind != CueKind.Damage), "A typed result suppresses the duplicate marked-damage-derived impact");
        string json = System.Text.Json.JsonSerializer.Serialize(prevented);
        var roundtrip = System.Text.Json.JsonSerializer.Deserialize<GameSnapshot>(json)!;
        check(roundtrip.Activity[0].Impact is { Amount: 2, PlayerId: 1, CardId: null, Infect: false }
            && roundtrip.Activity[1].Impact is { Kind: "life", Amount: -2 },
            "Additive impact contracts survive a native JSON round trip with their recipient and damage/life distinction intact");
        var anonymous = result with { Players = result.Players.Select(p => p.Id != 1 ? p : p with { Zones = p.Zones.Select(z => z.Name != "Battlefield" ? z : z with {
            Cards = z.Cards.Select(c => c.VisualId != CounterDamageScenario.TargetId ? c : c with { VisualId = "", CombatId = "anonymous-recipient", FaceDown = true, Name = "Face-down card", Text = "" }).ToImmutableArray()
        }).ToImmutableArray() }).ToImmutableArray(), Activity = [new(100, 8, "MAIN1", "damage", 1, "Anonymous damage", null, null) { Impact = new("damage", 3, "anonymous-recipient", "Face-down card") }] };
        feedback.Observe(feedbackActivity.Observe(null, baseline), 0); feedback.Observe(feedbackActivity.Observe(baseline, anonymous), 8);
        check(feedback.Cues is [{ Impact: { Amount: 3, CardId: "anonymous-recipient" } }],
            "A public anonymous recipient retains its opaque combat occurrence without acquiring a disclosed face");
    }

    internal static void RunPresentation(string profile, Action<bool, string> check)
    {
        foreach (bool reduced in new[] { false, true }) {
            string directory = Path.Combine(profile, "actual-results-" + reduced);
            Directory.CreateDirectory(directory);
            new PlayPreferences { Automatic = false, ReducedMotion = reduced, Sounds = false, TextScale = 1.3f }.Save(directory);
            var stages = DamageResultsScenario.Create();
            var engine = new InteractionScenarioEngine(); engine.Present(stages[0].Snapshot);
            using var app = new TableApplication(TestTableServices.For(engine), directory, initialState: engine.State) { Automated = true };
            app.Initialize(); app.SetTableStudy(true);
            Mana.Table.Presentation.PresentationFrame Tick(double seconds) {
                var time = new Microsoft.Xna.Framework.GameTime(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(.01));
                app.Update(time, new(20, 60, 0, default, default, default, default, default), new(), new(1600, 900));
                return app.ReadPresentation();
            }
            Tick(10);
            engine.Present(stages[1].Snapshot); app.TestPort.Accept(engine.State);
            var frame = Tick(10.2);
            var receipt = app.TestPort.Impacts.SingleOrDefault(i => i.CardId == CounterDamageScenario.TargetId);
            check(receipt is { Kind: "damage", Amount: 3 }
                && frame.Cards.All(card => !card.Display.Bounds.Intersects(receipt.Bounds))
                && app.TestPort.Match!.Players[1].Zone("Battlefield").Cards.Single(c => c.VisualId == CounterDamageScenario.TargetId) is { Power: 6, Toughness: 6, Damage: 3 }
                && !frame.Hits.Any(hit => hit.Id == "action-details")
                && !frame.Observe().Text.Any(text => text.Value is "JUST RESOLVED" or "PUBLIC OUTCOME"),
                "The actual result composition anchors three damage outside its surviving recipient, keeps settled 6/6 and three marked damage, and does not cover the table with routine success prose; receipt "
                + receipt + "; intersections " + string.Join(", ", frame.Cards.Where(card => receipt != null && card.Display.Bounds.Intersects(receipt.Bounds)).Select(card => card.Id)));
            engine.Present(stages[2].Snapshot); app.TestPort.Accept(engine.State); Tick(10.5);
            check(app.TestPort.Impacts.Any(i => i is { CardId: CounterDamageScenario.TargetId, Amount: 3 })
                && app.TestPort.Match!.Players[1].Zone("Graveyard").Cards.Any(c => c.VisualId == CounterDamageScenario.TargetId),
                "A public lethal recipient still receives the actual damage impact at its departure rather than disappearing between observations");
            engine.Present(stages[3].Snapshot); app.TestPort.Accept(engine.State); Tick(15);
            engine.Present(stages[4].Snapshot); app.TestPort.Accept(engine.State); frame = Tick(15.2);
            check(app.TestPort.Impacts.Any(i => i is { Kind: "damage", Amount: 2, PlayerId: 1, CardId: null })
                && app.TestPort.Impacts.Any(i => i is { Kind: "life", Amount: -2, PlayerId: 1 })
                && app.TestPort.Match!.Players[1].Life == 38 && engine.Replies.Count == 0,
                "The actual " + (reduced ? "reduced-motion" : "animated") + " composition presents the reported two damage and separate life transition at the correct player, without treating base three as dealt or submitting commands");
            Tick(19);
            check(app.TestPort.Impacts.Count == 0 && app.TestPort.Match!.Players[1].Life == 38,
                "Impact feedback expires while the current life state remains authoritative");
            CheckGroupedRecipients(directory, reduced, check);
        }
    }

    private static void CheckGroupedRecipients(string profile, bool reduced, Action<bool, string> check)
    {
        var ids = new[] { "group-damage-a", "group-damage-b", "group-damage-c" };
        var template = DamageResultsScenario.Create()[0].Snapshot;
        var engine = new InteractionScenarioEngine();
        GameSnapshot Before(string name, int[] marked) => template with {
            Id = "group-damage-" + name, Stack = [], Activity = [], Combat = null,
            Decision = new() { Kind = "input", Intent = DecisionIntent.Priority },
            Players = template.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                Count = player.Id == 1 ? 3 : 0,
                Cards = (player.Id != 1 ? [] : ids.Select((id, index) => new Card {
                    VisualId = id, CombatId = id, Name = "Cat Token", Type = "Token Creature — Cat", Power = 3, Toughness = 5, Damage = marked[index]
                }).ToArray()).ToImmutableArray()
            }).ToImmutableArray() }).ToImmutableArray()
        };
        GameSnapshot After(GameSnapshot before, int marked, params (int Card, int Amount)[] hits) => before with {
            Players = before.Players.Select(player => player with { Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                Cards = zone.Cards.Select(card => card with { Damage = marked }).ToImmutableArray()
            }).ToImmutableArray() }).ToImmutableArray(),
            Activity = hits.Select((hit, index) => new ActivityEntry(index + 1, 8, "MAIN1", "damage", 1, "Typed actual damage", ids[hit.Card], "Cat Token") {
                Impact = new("damage", hit.Amount, ids[hit.Card], "Cat Token", DamageType: "normal")
            }).ToImmutableArray()
        };
        var uniform = Before("uniform", [0, 0, 1]);
        engine.Present(uniform);
        string directory = Path.Combine(profile, "grouped-recipients");
        Directory.CreateDirectory(directory);
        new PlayPreferences { Automatic = false, ReducedMotion = reduced, Sounds = false, TextScale = 1.3f }.Save(directory);
        using var app = new TableApplication(TestTableServices.For(engine), directory, initialState: engine.State) { Automated = true };
        app.Initialize(); app.SetTableStudy(true);
        Mana.Table.Presentation.PresentationFrame Tick(double seconds) {
            var time = new Microsoft.Xna.Framework.GameTime(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(.01));
            app.Update(time, new(20, 60, 0, default, default, default, default, default), new(), new(1600, 900));
            return app.ReadPresentation();
        }
        void Present(GameSnapshot state) { engine.Present(state); app.TestPort.Accept(engine.State); }
        Tick(10);
        Present(After(uniform, 1, (0, 1), (1, 1))); var frame = Tick(10.2);
        var one = app.TestPort.Impacts.SingleOrDefault();
        check(one is { Amount: 1, GroupMembers: 3, Caption: "damage each ·2/3" }
            && one.AffectedCardIds.Order().SequenceEqual(ids.Take(2)) && one.DisplayId == ids[2]
            && frame.Cards.All(card => !card.Display.Bounds.Intersects(one.Bounds)),
            "Two of three matching visible creatures share one actual one-damage-each receipt with their exact affected subset, not two duplicate labels or summed two damage on one creature");
        app.TestPort.OpenBattlefield(1); Tick(10.3);
        check(app.TestPort.Impacts is [{ Amount: 1, GroupMembers: 3, Caption: "damage each ·2/3" } focused]
            && focused.AffectedCardIds.Order().SequenceEqual(ids.Take(2)),
            "The focused table keeps the same per-copy damage amount and exact two-of-three membership on its displayed group");

        var varied = Before("varied", [1, 0, 2]); Present(varied); Tick(20);
        Present(After(varied, 2, (0, 1), (1, 2))); Tick(20.2);
        var distinct = app.TestPort.Impacts.ToArray();
        check(distinct.Length == 2 && distinct.All(cue => cue.GroupMembers == 3 && cue.Caption == "damage ·1/3")
            && distinct.Any(cue => cue.Amount == 1 && cue.AffectedCardIds.SequenceEqual([ids[0]]))
            && distinct.Any(cue => cue.Amount == 2 && cue.AffectedCardIds.SequenceEqual([ids[1]]))
            && !distinct[0].Bounds.Intersects(distinct[1].Bounds),
            "Different actual per-creature amounts retain separate exact subgroup receipts rather than inventing one total or uniform damage amount");

        var repeated = Before("same-recipient-hits", [0, 0, 2]); Present(repeated); Tick(30);
        Present(After(repeated, 2, (0, 1), (0, 1), (1, 2))); Tick(30.2);
        check(app.TestPort.Impacts is [{ Amount: 2, GroupMembers: 3, Caption: "damage each ·2/3" } summed]
            && summed.AffectedCardIds.Order().SequenceEqual(ids.Take(2)),
            "Multiple same-observation hits on one occurrence sum for that recipient before uniform per-copy grouping");

        var concealed = app.TestPort.Match! with { Players = app.TestPort.Match!.Players.Select(player => player.Id != 1 ? player : player with {
            Eliminated = true,
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with { Count = 0, Cards = [] }).ToImmutableArray()
        }).ToImmutableArray() };
        Present(concealed); Tick(30.4);
        check(app.TestPort.Impacts.Count == 0 && engine.Replies.Count == 0,
            "Grouped impact receipts disappear with a conceded seat's removed public battlefield without retaining a face or issuing commands");

        var focus = Before("focused-offscreen", [0, 0, 0]) with { Players = Before("focused-offscreen", [0, 0, 0]).Players.Select(player => player.Id != 1 ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                Count = 15, Cards = Enumerable.Range(0, 15).Select(index => new Card { VisualId = "focus-impact-" + index,
                    CombatId = "focus-impact-" + index, Name = "Distinct creature " + index, Type = "Creature", Power = 3, Toughness = 5 }).ToImmutableArray()
            }).ToImmutableArray()
        }).ToImmutableArray() };
        // The shared-table focus fits a duel-like field; it does not open the
        // scrollable battlefield workspace whose offscreen aliases this case
        // exercises. Select that actual workspace explicitly.
        Present(focus); app.SetTableStudy(false); Tick(40); app.TestPort.OpenBattlefield(1); frame = Tick(40.1);
        check(app.TestPort.ZoneName == "Battlefield"
            && frame.Hits.Any(hit => hit.Id == "battlefield-focus-card:focus-impact-0")
            && !frame.Hits.Any(hit => hit.Id == "battlefield-focus-card:focus-impact-14"),
            "The offscreen damage case opens the actual scrollable battlefield workspace with its first recipient visible and last recipient outside the viewport");
        var offscreen = focus with { Activity = [new(1, 8, "MAIN1", "damage", 1, "Damage outside the focused viewport", "focus-impact-14", "Distinct creature 14") {
            Impact = new("damage", 1, "focus-impact-14", "Distinct creature 14", DamageType: "normal")
        }] };
        Present(offscreen); Tick(40.2);
        check(app.TestPort.ZoneName == "Battlefield" && app.TestPort.Impacts.Count == 0,
            "An offscreen focused recipient never falls back to its covered whole-table pose and paints damage over an unrelated visible tile");
    }
}
