using Mana.Contracts;
using Mana.Magic;
using Mana.Renderer;
using Mana.Scenarios;
using Microsoft.Xna.Framework;

namespace Mana.Table;

internal static class SharedTableStudyChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var stages = FourPlayerTableScenario.Create();
        var ordinary = stages[0].Snapshot;
        var crowded = stages[1].Snapshot;
        foreach (int viewer in ordinary.Players.Select(player => player.Id)) {
            var quiet = Build(ordinary with { ViewerId = viewer });
            var busy = Build(crowded with { ViewerId = viewer });
            var quietPoses = Cards(quiet).ToDictionary(card => card.Id, card => card.Pose);
            check(Cards(busy).Where(card => card.Owner != 2).All(card => quietPoses[card.Id] == card.Pose)
                    && Cards(busy).Count(card => card.Owner == 2) > Cards(quiet).Count(card => card.Owner == 2),
                $"Study viewer {viewer}: one crowded controller leaves every other controller's actual battlefield poses unchanged");

            var pending = crowded with { ViewerId = viewer, Stack = stages[2].Snapshot.Stack,
                PriorityPlayerId = (viewer + 1) % 4, DecisionOwnerId = viewer };
            var busyPoses = Cards(busy).ToDictionary(card => card.Id, card => card.Pose);
            check(Cards(Build(pending)).All(card => busyPoses[card.Id] == card.Pose),
                $"Study viewer {viewer}: a pending cross-seat effect and changed responder never reflow the battlefield");
            CheckFootprints(quiet, "ordinary viewer " + viewer, check);
            CheckFootprints(busy, "one crowded controller viewer " + viewer, check);
        }

        var canonical = ClutteredChangesScenario.Initial();
        var canonicalScene = Build(canonical);
        CheckFootprints(canonicalScene, "canonical crowded table", check);
        check(Cards(canonicalScene).Count() == canonical.Players.Sum(player => player.Zone("Battlefield").Cards.Length)
                && canonicalScene.Seats.All(seat => seat.Battlefield.SelectMany(cluster => cluster.Cards).All(node =>
                    ReferenceEquals(node.Card, canonical.Players.Single(player => player.Id == node.Owner)
                        .Zone("Battlefield").Cards.Single(card => card.VisualId == node.Id))
                    && ReferenceEquals(seat.BattlefieldCard(node.Id), node))),
            "The crowded study retains each projected occurrence and its exact current card for lookup and input");

        CopyStates(ordinary, check);
        HiddenFace(check);
        check(TableScene.Build(ordinary, 0, new(-100, -100), null, null, false, CardPresentation.IsResource)
                .Seats.All(seat => seat.Battlefield.SelectMany(cluster => cluster.Cards).All(card => !card.ScanFace)),
            "The explicit study leaves the default battlefield presentation unchanged");
    }

    private static void CheckFootprints(TableScene scene, string label, Action<bool, string> check)
    {
        var outside = scene.Seats.SelectMany(seat => seat.Battlefield.SelectMany(cluster => cluster.Cards.Select(card => new {
            Node = card, Seat = seat.Index, Role = cluster.Kind,
            Lane = SharedTableStudy.Lane(seat.Index, cluster.Kind), Territory = SharedTableStudy.Territory(seat.Index)
        }))).FirstOrDefault(entry => !entry.Lane.Contains(entry.Node.Pose.Bounds)
            || !entry.Territory.Contains(entry.Node.Pose.Bounds)
            || !entry.Node.Pose.Contains(entry.Node.Pose.Center.ToPoint())
            || entry.Node.Pose.IsProjected || !entry.Node.ScanFace
            || entry.Node.Pose.Angle != (entry.Node.Card.Tapped ? MathF.PI / 2 : 0));
        check(outside == null,
            "Study " + label + ": every upright or tapped face and its input geometry fit its controller's reserved role lane"
            + (outside == null ? "" : $"; first offender {outside.Node.Id} ({outside.Node.Card.Name}), controller {outside.Node.Owner}, seat {outside.Seat}, role {outside.Role}, bounds {outside.Node.Pose.Bounds}, lane {outside.Lane}, territory {outside.Territory}, pose {outside.Node.Pose}, corners {string.Join(";", outside.Node.Pose.Corners)}"));
        var cards = Cards(scene).ToArray();
        var overlap = cards.SelectMany(card => cards.Where(other => other.Owner != card.Owner && card.Pose.Bounds.Intersects(other.Pose.Bounds))
            .Select(other => new { First = card, Second = other })).FirstOrDefault();
        var actionOverlap = cards.FirstOrDefault(card => card.Pose.Bounds.Intersects(SharedTableStudy.ActionArea));
        check(overlap == null && actionOverlap == null,
            "Study " + label + ": controller faces remain separate from other players and the fixed action area"
            + (overlap == null ? "" : $"; {overlap.First.Id} controller {overlap.First.Owner} bounds {overlap.First.Pose.Bounds} intersects {overlap.Second.Id} controller {overlap.Second.Owner} bounds {overlap.Second.Pose.Bounds}")
            + (actionOverlap == null ? "" : $"; {actionOverlap.Id} bounds {actionOverlap.Pose.Bounds} intersects action area {SharedTableStudy.ActionArea}"));
        var covered = scene.Seats.SelectMany(seat => seat.Geometry.Piles.Select(pile => new { Owner = seat.PlayerId, Kind = pile.Zone, Bounds = pile.Pose.Bounds })
                .Concat(seat.Geometry.Commanders.Select(card => new { Owner = seat.PlayerId, Kind = "Command:" + card.Id, Bounds = card.Pose.Bounds })))
            .SelectMany(footprint => cards.Where(card => footprint.Bounds.Intersects(card.Pose.Bounds)).Select(card => new { Footprint = footprint, Card = card })).FirstOrDefault();
        check(covered == null,
            "Study " + label + ": public zone piles and command cards do not cover any controller's battlefield faces"
            + (covered == null ? "" : $"; {covered.Footprint.Kind} controller {covered.Footprint.Owner} bounds {covered.Footprint.Bounds} covers {covered.Card.Id} controller {covered.Card.Owner} bounds {covered.Card.Pose.Bounds}"));
    }

    private static void CopyStates(GameSnapshot baseline, Action<bool, string> check)
    {
        Card Copy(string id) => new() { VisualId = id, CombatId = id, Name = "Cat", Type = "Token Creature — Cat", Power = 2, Toughness = 2 };
        Card[] copies = [Copy("copy-a"), Copy("copy-b"), Copy("copy-c"),
            Copy("counter-copy") with { Power = 3, Toughness = 3, Counters = new() { ["+1/+1"] = 1 } },
            Copy("tapped-copy") with { Tapped = true }, Copy("damaged-copy") with { Damage = 1 },
            Copy("phased-copy") with { PhasedOut = true }, Copy("flying-copy") with { CombatKeywords = ["flying"] },
            Copy("attached-copy") with { Attachments = [new("sword", "Sword of the Animist", "Equipment")] },
            Copy("hidden-a") with { FaceDown = true, Name = "Face-down permanent" },
            Copy("hidden-b") with { FaceDown = true, Name = "Face-down permanent" }];
        var state = ReplaceField(baseline, 0, copies);
        var scene = Build(state);
        var placed = Cards(scene).Where(card => card.Owner == 0).ToArray();
        check(placed.Length == copies.Length && placed.Select(card => card.Id).Distinct().Count() == copies.Length
                && placed.Select(card => card.Pose.Center).Distinct().Count() == copies.Length
                && placed.All(node => ReferenceEquals(node.Card, copies.Single(card => card.VisualId == node.Id)))
                && placed.Where(card => card.Id.StartsWith("copy-", StringComparison.Ordinal)).All(card => card.GroupCount == 3)
                && placed.Count(card => card.Id.StartsWith("copy-", StringComparison.Ordinal) && card.GroupFront) == 1
                && placed.Where(card => !card.Id.StartsWith("copy-", StringComparison.Ordinal)).All(card => card.GroupCount == 1 && card.GroupFront),
            "The study fans equivalent copies without losing occurrences, while counters, tap, damage, phase, abilities, attachments and hidden identities retain separate current-state faces");
        var ordered = state with { Decision = new() { Id = "order-copies", Kind = "choice", Ordered = true,
            Choices = copies.Take(3).Select((card, index) => new Choice(index, card.Name, card) { CardId = card.VisualId }).ToArray() } };
        var choices = Cards(Build(ordered)).Where(card => card.Owner == 0 && card.Id.StartsWith("copy-", StringComparison.Ordinal)).ToArray();
        check(choices.Length == 3 && choices.All(card => card.GroupCount == 1 && card.GroupFront)
                && choices.Select(card => card.Pose.Center).Distinct().Count() == 3,
            "Ordered equivalent copies retain each actual candidate's distinct position instead of becoming one shared choice face");
    }

    private static void HiddenFace(Action<bool, string> check)
    {
        // Defensive painter coverage: a stale private face must remain masked
        // even if an invalid upstream projection accidentally carries its text.
        var hidden = new Card { VisualId = "study-hidden", Key = "study-hidden-key", FaceDown = true,
            Name = "HIDDEN_PRINTED_IDENTITY", ArtName = "HIDDEN_ART_IDENTITY", ManaCost = "HIDDEN_COST",
            Text = "HIDDEN_PRINTED_RULES", Type = "Creature", Power = 2, Toughness = 2, PhasedOut = true };
        var lab = new ExperienceLabSession(FourPlayerTableScenario.Id);
        var engine = new ExperienceLabEngine(lab);
        string profile = Path.Combine(Path.GetTempPath(), "ManaTableSharedTableStudyChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = true }.Save(profile);
        using var app = new TableApplication(engine, profile, initialState: lab.State) { Automated = true };
        app.Initialize();
        app.TestPort.SetTableStudy(true);
        app.TestPort.Accept(ReplaceField(lab.State, 2, [hidden]));
        var frame = app.ComposePresentation(new GameTime(TimeSpan.FromSeconds(1), TimeSpan.Zero));
        var text = frame.Drawing.Commands.Select(command => command switch {
            DrawCommand.Text value => value.Value, DrawCommand.CenterText value => value.Value, _ => null
        }).Where(value => value != null).ToArray();
        check(app.TestPort.TableStudyEnabled && frame.Scene!.BattlefieldCard(2, hidden.VisualId) is { ScanFace: true, Card.FaceDown: true }
                && CardPresentation.Artwork(hidden) == null && text.Contains("Face-down permanent")
                && text.All(value => !value!.Contains("HIDDEN_", StringComparison.Ordinal)),
            "The production study painter masks a hidden printed name, artwork, cost and rules while retaining its public anonymous creature state");
        check(frame.Cards.Where(card => card.Zone == "Battlefield").All(card => {
                int seat = frame.Scene!.Seat(card.Request.Owner!.Value).Index;
                return SharedTableStudy.Lane(seat, BattlefieldLayout.Role(card.Card, CardPresentation.IsResource)).Contains(card.Rest.Bounds);
            }),
            "The composed study's actual resting card/input poses remain inside their role lanes, including flying and phased state presentation");
    }

    private static GameSnapshot ReplaceField(GameSnapshot state, int owner, Card[] cards) => state with {
        Players = state.Players.Select(player => player.Id != owner ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with { Count = cards.Length, Cards = cards }).ToArray()
        }).ToArray()
    };
    private static IEnumerable<SceneCard> Cards(TableScene scene) => scene.Seats.SelectMany(seat => seat.Battlefield).SelectMany(cluster => cluster.Cards);
    private static TableScene Build(GameSnapshot state) => TableScene.Build(state, 0, new(-100, -100), null, null,
        false, CardPresentation.IsResource, sharedTableStudy: true);
}
