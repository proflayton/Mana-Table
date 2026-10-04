using Mana.Magic;
using System.Text.Json;
using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal static class PresentationSmoke
{
    public static void Run(string profile)
    {
        Directory.CreateDirectory(profile); var results = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); results.Add(message); }
        var state = Example();
        var scenePages = new Dictionary<string, int> { ["1:permanents"] = 999 };
        var scene = TableScene.Build(state, scenePages, 0, new(-100, -100), null, null, false, CardPresentation.IsResource);
        Check(scene.Rank(1, false).First == 3 && scenePages["1:permanents"] == 999, "Scene construction clamps crowded ranks without mutating UI paging state");
        Check(scene.Rank(1, false).Cards.Select(c => c.Id).Distinct().Count() == 5, "Duplicate card names produce distinct scene occurrences");
        Check(scene.Hand.Cards.All(c => c.Contains(c.Pose.Center.ToPoint())), "The immutable scene supplies the same hand pose for drawing and hit testing");
        Check(state.Players.All(p => scene.Commanders(p.Id).Count == 1 && scene.Piles(p.Id).Count == 3), "Every seat has command cards and zone piles in its scene description");
        var secondView = state with { ViewerId = 2 };
        Check(TableLayout.Opponents(secondView).Select(p => p.Id).SequenceEqual([3, 0, 1]) && TableLayout.SeatIndex(secondView, 0) == 1,
            "Changing the viewer rotates the same seating order around the shared table");
        var noStops = new HashSet<string>();
        Check(TurnGuide.CanAutoPass(state, true, false, noStops, false), "Engine-authorized empty priority can advance in Auto");
        var flier = new Card { Type = "Creature", CombatKeywords = ["flying", "vigilance"], Power = 4, Toughness = 4, Counters = new() { ["+1/+1"] = 3, ["Shield"] = 2, ["Time"] = 0 } };
        Check(CardPresentation.HasAbility(flier, "flying") && !CardPresentation.HasAbility(flier with { CombatKeywords = [], Text = "Flying" }, "flying"), "Effective ability markers use the engine projection, never printed rules text");
        Check(CardPresentation.Abilities(flier with { FaceDown = true }).Length == 0, "Concealed faces expose no ability markers");
        Check(CardPresentation.Counters(flier).Select(c => c.Value).SequenceEqual([3, 2]) && flier.Power == 4, "Counter stacks retain explicit counts without modifying the engine's current power/toughness");
        var ground = new Pose(new(700, 500), new(90, 126));
        var raised = CardMotion.Airborne(ground, 1, false, "flier");
        Check(raised.Center.Y < ground.Center.Y && raised.Elevation > 12 && raised.Contains(raised.Center.ToPoint()), "Airborne visuals and pointer geometry share the lifted pose");
        Check(CardMotion.Airborne(ground, 1, true, "flier") == CardMotion.Airborne(ground, 2, true, "flier"), "Reduced motion preserves flying height without bobbing");
        Check(!TurnGuide.CanAutoPass(state with { Decision = state.Decision! with { CanAutoPass = false } }, true, false, noStops, false), "Playable responses prevent Auto");
        Check(!TurnGuide.CanAutoPass(state, true, true, noStops, false), "Hold cancels Auto");
        Check(!TurnGuide.CanAutoPass(state, true, false, new HashSet<string> { "MAIN1" }, false), "Saved own-turn stops prevent Auto");
        Check(!TurnGuide.CanAutoPass(state, true, false, noStops, true), "Inspecting or dragging suspends Auto");
        Check(!TurnGuide.CanAutoPass(state with { Status = "finished" }, true, false, noStops, false), "Terminal snapshots never advance");
        Check(!TurnGuide.CanAutoPass(state with { Decision = state.Decision! with { Kind = "choice" } }, true, false, noStops, false), "Required choices cannot become automatic priority");
        var attack = state with { Decision = state.Decision! with { Intent = DecisionIntent.Attack }, Combat = new([], [new("elf", [new("player", "2", 2, "Second opponent"), new("card", "walker", 2, "Planeswalker")])], null) };
        Check(LegalTargets.CanAttack(attack, "elf", 2) && !LegalTargets.CanAttack(attack, "elf", 1), "Only projected legal defending players accept an attack");
        Check(LegalTargets.CanAttackCard(attack, "elf", "walker") && !LegalTargets.CanAttackCard(attack, "elf", "hidden-card"), "Card defenders require an explicit legal pair");
        var block = state with { Decision = state.Decision! with { Intent = DecisionIntent.Block }, Combat = new([new("opponent-creature", 0, [], ["elf"])], [], null) };
        Check(LegalTargets.CanBlock(block, "elf", "opponent-creature") && !LegalTargets.CanBlock(block, "other", "opponent-creature"), "Block assignments use the engine's eligible pairs");
        var pose = new Pose(new(200, 200), new(60, 100), .6f);
        Check(pose.Contains(new(200, 200)) && !pose.Contains(new(pose.Bounds.Left + 1, pose.Bounds.Top + 1)), "Rotated cards hit-test their face, not their bounding rectangle");
        foreach (int seat in new[] { 0, 1, 2, 3 }) {
            var center = TableWorld.SeatPoint(seat, 100, 300);
            Check(Vector2.Distance(center, TableWorld.Unproject(TableWorld.Project(center))) < .001f, $"Seat {seat} projects and unprojects on the same table plane");
            foreach (bool tapped in new[] { false, true }) {
                float angle = TableWorld.SeatAngle(seat) + (tapped ? MathF.PI / 2 : 0);
                var projected = TableWorld.Card(center, new(80, 112), angle);
                var local = new Vector2(-40, -56);
                var rotated = new Vector2(local.X * MathF.Cos(angle) - local.Y * MathF.Sin(angle), local.X * MathF.Sin(angle) + local.Y * MathF.Cos(angle));
                Check(Vector2.Distance(projected.Point(0, 0), TableWorld.Project(center + rotated)) < .001f
                    && projected.Contains(projected.Center.ToPoint()) && !projected.Contains(new(projected.Bounds.Left, projected.Bounds.Top)),
                    $"Seat {seat} {(tapped ? "tapped" : "untapped")} face, texture corners, and pointer shape share the projection");
            }
        }
        var near = TableWorld.Card(new(0, 300), new(80, 112)); var far = TableWorld.Card(new(0, -300), new(80, 112));
        Check(near.Bounds.Width > far.Bounds.Width && near.Bounds.Height > far.Bounds.Height, "Near cards grow in perspective while distant cards recede");
        Check(TableWorld.CanPlayAt(TableWorld.Project(new(0, 200)).ToPoint()) && !TableWorld.CanPlayAt(TableWorld.Project(new(-575, 0)).ToPoint()), "Playing from hand accepts the local physical battlefield and rejects an opponent's seat");
        foreach (int count in new[] { 1, 7, 11 }) {
            var positions = Enumerable.Range(0, count).Select(i => TableLayout.Hand(i, count)).ToArray();
            Check(positions.All(p => p.Bounds.Left >= 220 && p.Bounds.Right <= 1245) && positions.Select(p => p.Center.X).SequenceEqual(positions.Select(p => p.Center.X).Order()), $"{count}-card fan stays clear of player and action controls");
        }
        var motion = new SceneMotion(); var start = new Pose(new(0, 0), new(80, 112)); var end = start with { Center = new(400, 100) };
        motion.Begin(.016f, false); motion.Place("visible-id", start); motion.End();
        motion.Begin(.016f, false); var moving = motion.Place("visible-id", end); motion.End();
        Check(moving.Center.X > 0 && moving.Center.X < 400, "A stable visible identity animates across zones");
        Check(moving.Elevation > 0 && motion.TryGet("visible-id", out var drawn) && drawn == moving, "Travel lifts a card above the table and effects follow its displayed position");
        motion.Begin(.016f, false); var continued = motion.Place("visible-id", end); motion.End();
        Check(continued.Center.X > moving.Center.X, "Repeated snapshots do not restart motion");
        motion.Begin(.016f, true); var snapped = motion.Place("visible-id", end); motion.End();
        Check(snapped == end, "Reduced motion snaps directly to the authoritative target");
        motion.Begin(.016f, false); motion.End();
        Check(motion.Count == 0, "Objects leaving visible projection are removed immediately");
        Check(!motion.TryGet("visible-id", out _), "Effects cannot retrieve a pose removed from the current projection");
        motion.Begin(.016f, false); var delayed = motion.Place("dealt", end, start.Center, .1f); motion.End();
        Check(delayed.Center == start.Center, "Opening hand cards can be dealt with a staggered entrance");
        motion.Begin(.016f, true); var instant = motion.Place("dealt", end, start.Center, .1f); motion.End();
        Check(instant == end, "Reduced motion skips entrance delays as well as travel");
        motion.Begin(.016f, false); var heldPose = motion.Hold("dealt", start with { Center = new(280, 40), Elevation = 46 }); motion.End();
        Check(heldPose.Center == new Vector2(280, 40) && motion.TryGet("dealt", out var heldDisplay) && heldDisplay == heldPose, "A dragged card stays attached to the pointer and retains its displayed occurrence");
        motion.Begin(.016f, false); var releasedPose = motion.Place("dealt", end, new(0, 800)); motion.End();
        Check(releasedPose.Center.X > heldPose.Center.X && releasedPose.Center.X < end.Center.X, "Release continues from the held position rather than a new library entrance");
        var presence = new TablePresence();
        var afterDraw = state with { Players = state.Players.Select(p => p.Id == 1 ? p with { Zones = p.Zones.Select(z => z.Name == "Hand" ? z with { Count = z.Count + 1 } : z.Name == "Library" ? z with { Count = z.Count - 1 } : z).ToArray() } : p.Id == 2 ? p with { Life = p.Life - 3 } : p).ToArray() };
        presence.Observe(state, afterDraw, 5);
        Check(presence.Cues.Count == 2 && presence.PendingDraws(1, 5.2) == 1 && presence.Cues.Any(c => c.Kind == SeatCueKind.LifeLoss && c.Amount == 3), "Public count and life deltas create seat feedback without hidden card identities");
        presence.Observe(afterDraw, afterDraw, 5.1);
        Check(presence.Cues.Count == 2 && presence.PendingDraws(1, 6) == 0, "Repeated polls neither repeat nor prolong anonymous card draws");
        foreach (int seat in new[] { 0, 1, 2 }) {
            var library = TableWorld.Pile(seat, 0); var hand = TableLayout.HiddenHand(seat, 6, 7);
            var transfer = TablePresence.DrawPose(library, hand, .3);
            Check(TablePresence.DrawPose(library, hand, 0).Center == library.Center && Vector2.Distance(TablePresence.DrawPose(library, hand, TablePresence.DrawDuration).Center, hand.Center) < .001f && transfer.Elevation > 0,
                $"Seat {seat} draws from its physical library into its own hand with an airborne arc");
        }
        var returned = afterDraw with { Players = afterDraw.Players.Select(p => p.Id == 1 ? p with { Zones = p.Zones.Select(z => z.Name == "Hand" ? z with { Count = z.Count + 1 } : z).ToArray() } : p).ToArray() };
        presence.Observe(afterDraw, returned, 7);
        Check(presence.Cues.Count == 0, "A hand increase without a library decrease does not invent a draw");
        presence.Observe(state, afterDraw, 8); presence.Observe(afterDraw, afterDraw with { Id = "rematch" }, 8.1);
        Check(presence.Cues.Count == 0, "A rematch clears every seat's presentation cues");
        Check(TableWorld.Spell(0).Bounds.Height > 185 && TableWorld.Spell(0).Bounds.Bottom < 525 && TableWorld.Spell(0).Contains(TableWorld.Spell(0).Center.ToPoint()), "The response spell is readable and remains inside the shared center of the table");
        var edge = new Pose(new(100, 100), new(80, 120), MathF.PI / 2).EdgeToward(new(500, 100));
        Check(Math.Abs(edge.X - 160) < .01f && Math.Abs(edge.Y - 100) < .01f, "Combat connections meet rotated card edges instead of covering text");
        var damaged = state with { Players = state.Players.Select(p => p.Id != 0 ? p : p with { Zones = p.Zones.Select(z => z.Name != "Battlefield" ? z : z with { Cards = z.Cards.Select((c, i) => i == 1 ? c with { Damage = 2, Tapped = true } : c).ToArray() }).ToArray() }).ToArray() };
        var effects = new SceneEffects(); effects.Observe(state, damaged, 1);
        Check(effects.Cards.Count == 2 && effects.Cards.Any(c => c.Kind == CueKind.Damage && c.Amount == 2), "Damage and tapping feedback use authoritative deltas");
        effects.Observe(damaged, damaged, 1.1);
        Check(effects.Cards.Count == 2, "Repeated snapshots do not duplicate feedback");
        var concealed = damaged with { Players = damaged.Players.Select(p => p.Id != 0 ? p : p with { Zones = p.Zones.Select(z => z with { Cards = z.Cards.Select(c => c.VisualId == "seat-0-1" ? c with { FaceDown = true } : c).ToArray() }).ToArray() }).ToArray() };
        effects.Observe(damaged, concealed, 1.2);
        Check(effects.Cards.Count == 0, "Concealing a card immediately removes its remaining visual effects");
        effects.Observe(state, damaged, 2); effects.Observe(damaged, damaged with { Id = "another-game" }, 2.1);
        Check(effects.Cards.Count == 0, "A new match discards feedback from the old game");
        var bird = new Card { VisualId = "new-token", Name = "Bird Token", Type = "Token Creature - Bird", Power = 1, Toughness = 1, CombatKeywords = ["flying"] };
        var tokenArrival = state with { Players = state.Players.Select(p => p.Id == 0 ? p with { Zones = p.Zones.Select(z => z.Name == "Battlefield" ? z with { Count = z.Count + 1, Cards = [.. z.Cards, bird] } : z).ToArray() } : p).ToArray() };
        effects.Clear(); effects.Observe(state, tokenArrival, 3);
        Check(effects.Cards.Any(c => c.VisualId == bird.VisualId && c.Kind == CueKind.Arrive), "A newly created token receives an arrival cue without needing a previous card occurrence");
        var narrative = new ActionFeedback();
        var resolved = state with { Activity = [new(1, state.Turn, state.PhaseKey, "resolved", 1, "Ravenform resolved.", "raven", "Ravenform") { Detail = "The public spell description." }] };
        narrative.Observe(state, resolved, 3); narrative.Observe(resolved, resolved, 4);
        Check(narrative.Last is { Started: 3, Detail: "The public spell description." }, "Repeated polls do not restart or replace resolution feedback");
        narrative.Observe(resolved, resolved with { Id = "rematch" }, 5);
        Check(narrative.Last == null, "Resolution feedback cannot leak into the next match");
        var ability = state.Viewer!.Zone("Battlefield").Cards.First(c => c.Selectable);
        Check(CardActions.CanActivate(state, ability) && !CardActions.CanActivate(state, ability with { FaceDown = true }), "Activation cues require an authorized visible battlefield occurrence");
        Check(!CardActions.CanActivate(state with { Decision = state.Decision! with { Intent = DecisionIntent.Block } }, ability), "A blocker selection is never advertised as activating an ability");
        var targeting = state with { Stack = [new("Ravenform", "Exile target creature", false, null) { Id = "spell", Targets = [new("card", ability.VisualId, ability.Name)] }] };
        var actionPacing = new PresentationPacing(); actionPacing.Observe(state, targeting, 10);
        Check(!actionPacing.Ready(12.4) && actionPacing.Ready(12.6), "Targeted spells get a readable telegraph before automatic resolution");
        var retargeted = targeting with { Stack = [targeting.Stack[0] with { Targets = [new("player", "1", "Opponent 1", 1)] }] };
        Check(PresentationPacing.ShouldPresent(targeting, retargeted), "Changing targets on an existing stack item is a visible checkpoint");
        Check(!CombatGuide.CanConfirm(block with { Combat = block.Combat! with { BlockProblem = "Two blockers required" } }), "Blocking requirements disable confirmation even when a generic OK is enabled");
        var prefs = new PlayPreferences { Automatic = false, ReducedMotion = true, Stops = ["MAIN1", "COMBAT_BEGIN"] };
        prefs.Save(profile); var loaded = PlayPreferences.Load(profile);
        Check(!loaded.Automatic && loaded.ReducedMotion && loaded.Stops.SetEquals(prefs.Stops), "Control and motion preferences survive restart");

        var options = new JsonSerializerOptions { WriteIndented = true };
        void Fixture(string name, GameSnapshot value) => File.WriteAllText(Path.Combine(profile, name + ".json"), JsonSerializer.Serialize(value, options));
        Fixture("battlefield", state);
        Fixture("card-states", StateExample());
        Fixture("combat", attack);
        Fixture("library-search", state with { Decision = new() { Id = "library-prompt", Kind = "choice", Context = "librarySearch", Title = "Search your library", Message = "Choose a basic land. Only the revealed selection is available.", Min = 1, Max = 1,
            Choices = [new(0, "Forest", Card("forest-search", "Forest", "Basic Land — Forest"))],
            LibraryCards = [new(0, "Forest", Card("forest-search", "Forest", "Basic Land — Forest")), new(null, "Llanowar Elves", Card("elf-search", "Llanowar Elves", "Creature — Elf Druid"))] } });
        Fixture("color-choice", state with { Decision = new() { Id = "color", Kind = "choice", Title = "Choose a color", Message = "Add one mana of the chosen color.", Min = 1, Max = 1, Choices = new[] { "W", "U", "B", "R", "G" }.Select((c, i) => new Choice(i, c, null) { Mana = "{" + c + "}" }).ToArray() } });
        Fixture("allocation", state with { Decision = new() { Id = "damage", Kind = "allocate", Message = "Assign seven damage among these targets.", Amount = 7, Limits = [7, 7, 7], Choices = [new(0, "Llanowar Elves", null), new(1, "Opponent 2", null), new(2, "Opponent 3", null)] } });
        File.WriteAllText(Path.Combine(profile, "presentation-results.json"), JsonSerializer.Serialize(results, options));
    }
    private static Card Card(string id, string name, string type) => new() { Key = "p:" + id, VisualId = id, CombatId = id, Name = name, Type = type, Power = type.Contains("Creature") ? 1 : null, Toughness = type.Contains("Creature") ? 1 : null, Selectable = true };
    internal static GameSnapshot StateExample()
    {
        var state = Example();
        string[][] abilities = [["flying", "vigilance"], ["deathtouch", "lifelink"], ["indestructible", "reach"], ["double strike", "trample", "menace"]];
        return state with { Decision = state.Decision! with { CanAutoPass = false }, Players = state.Players.Select(p => p with { Zones = p.Zones.Select(z => z.Name != "Battlefield" ? z : z with {
            Cards = z.Cards.Select((c, i) => !c.Type.Contains("Creature") ? c : c with {
                CombatKeywords = abilities[(i / 2) % abilities.Length], Selectable = false,
                Counters = i == 1 ? new() { ["+1/+1"] = 3, ["Shield"] = 2 } : i == 5 ? new() { ["-1/-1"] = 1 } : [],
                Power = i == 1 ? 4 : i == 5 ? 0 : 1, Toughness = i == 1 ? 4 : i == 5 ? 1 : 1, Sick = i == 7
            }).ToArray() }).ToArray() }).ToArray() };
    }
    internal static GameSnapshot Example()
    {
        var players = Enumerable.Range(0, 4).Select(i => new Player(i, i == 0 ? "You" : "Opponent " + i, 40, i == 0, false, [], [
            new("Hand", 7, i == 0 ? Enumerable.Range(0, 7).Select(n => Card("hand-" + n, n % 2 == 0 ? "Forest" : "Llanowar Elves", n % 2 == 0 ? "Basic Land — Forest" : "Creature — Elf Druid")).ToArray() : [], null),
            new("Battlefield", 16, Enumerable.Range(0, 16).Select(n => Card("seat-" + i + "-" + n, n % 2 == 0 ? "Forest" : "Llanowar Elves", n % 2 == 0 ? "Basic Land — Forest" : "Creature — Elf Druid") with { Tapped = n % 4 == 0, Selectable = i == 0 }).ToArray(), null),
            new("Command", 1, [Card("commander-" + i, "Goreclaw, Terror of Qal Sisma", "Legendary Creature — Bear") with { Selectable = i == 0 }], null),
            new("Library", 74, [], null), new("Graveyard", 1, [Card("grave-" + i, "Llanowar Elves", "Creature — Elf Druid")], null), new("Exile", 0, [], null)
        ]) { CommanderDamage = Enumerable.Range(0, 4).Where(n => n != i).Select(n => new CommanderDamage("Goreclaw, Terror of Qal Sisma", n, n == 0 ? "You" : "Opponent " + n, 0)).ToArray() }).ToArray();
        return new("presentation", "playing", null, null, 0, 17, "Main phase, precombat", "MAIN1", 0, players, [], null,
            new() { Id = "p", Kind = "input", Intent = DecisionIntent.Priority, Message = "You have priority.", Ok = "OK", OkEnabled = true, Cancel = "End Turn", CancelEnabled = true, CanAutoPass = true });
    }
}
