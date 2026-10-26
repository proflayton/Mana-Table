using System.Collections.Immutable;
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
        NameFamilies(ordinary, check);
        FocusedRows(crowded, check);
        HiddenFace(check);
        TwoPlayerContinuity(check);
        ResourceTurns(check);
        check(TableScene.Build(ordinary, new(-100, -100), null, null, false, CardPresentation.IsResource)
                .Seats.All(seat => seat.Battlefield.SelectMany(cluster => cluster.Cards).All(card => !card.ScanFace)),
            "The explicit study leaves the default battlefield presentation unchanged");
    }

    private static void TwoPlayerContinuity(Action<bool, string> check)
    {
        var stages = ExperienceScenarios.Find("two-player-table.v1").Create();
        foreach (int viewer in stages[0].Snapshot.Players.Select(player => player.Id)) {
            var scenes = stages.Select(stage => Build(stage.Snapshot with { ViewerId = viewer })).ToArray();
            check(scenes.All(scene => scene.Seat(viewer).Index == 3 && scene.Seats.Single(seat => seat.PlayerId != viewer).Index == 1
                    && scene.Seats.All(seat => !seat.IsDense) && Cards(scene).All(card => card.ScanFace)
                    && Cards(scene).All(card => DuelTableStudy.MatchLane(scene.Seat(card.Owner).Index,
                        BattlefieldLayout.Role(card.Card, CardPresentation.IsResource)).Contains(card.Pose.Bounds))
                    && Cards(scene).All(card => !card.Pose.Bounds.Intersects(DuelTableStudy.ActionArea))),
                "Both two-player viewers retain actual player IDs, top/bottom framing and source-card faces in their role lanes across the old seven-group boundary");
            check(scenes.All(scene => !Cards(scene).Any(card => Cards(scene).Any(other => card.Owner != other.Owner
                    && card.Pose.Bounds.Intersects(other.Pose.Bounds)))),
                "The two-player creature rows keep opposing faces and their current stats separate");
            var before = Cards(scenes[0]).ToDictionary(card => card.Id, card => card.Pose);
            check(Cards(scenes[1]).Where(card => card.Owner == 1 || card.Card.Type is "Artifact" or "Enchantment" or "Basic Land")
                    .All(card => before[card.Id] == card.Pose)
                    && Cards(scenes[2]).Where(card => card.Owner == 0).All(card => scenes[1].BattlefieldCard(card.Owner, card.Id)!.Pose == card.Pose),
                "Adding an eighth group changes only its controller's creature row; the opponent, unrelated role lanes and subsequent other-controller counter updates do not reframe the table");
        }
    }

    private static void ResourceTurns(Action<bool, string> check)
    {
        var stages = TappedResourcesScenario.Create();
        foreach (int viewer in stages[0].Snapshot.Players.Select(player => player.Id)) {
            var tapped = Build(stages[0].Snapshot with { ViewerId = viewer });
            var ready = Build(stages[2].Snapshot with { ViewerId = viewer });
            foreach (var card in Cards(tapped).Where(card => card.Id.StartsWith("tap-land-", StringComparison.Ordinal))) {
                var untapped = ready.BattlefieldCard(card.Owner, card.Id)!;
                check(card.Pose.Center == untapped.Pose.Center && card.Pose.Size == untapped.Pose.Size
                    && card.Pose.Size.Y > card.Pose.Size.X * 1.5f
                    && MathF.Abs(MathHelper.WrapAngle(card.Pose.Angle - untapped.Pose.Angle) - MathF.PI / 2) < .001f
                    && Math.Abs(card.Pose.Bounds.Width - untapped.Pose.Bounds.Height) <= 2
                    && Math.Abs(card.Pose.Bounds.Height - untapped.Pose.Bounds.Width) <= 2,
                    "Tapping a resource turns the same portrait face sideways without swapping dimensions or moving its reserved slot: " + card.Id);
            }
            foreach (var stage in stages) CheckFootprints(Build(stage.Snapshot with { ViewerId = viewer }), "resource turns viewer " + viewer, check);
        }
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
            || MathF.Abs(MathHelper.WrapAngle(entry.Node.Pose.Angle - SharedTableStudy.Facing(entry.Seat)
                - (entry.Node.Card.Tapped ? MathF.PI / 2 : 0))) > .001f);
        check(outside == null,
            "Study " + label + ": every seat-facing or tapped face and its input geometry fit its controller's reserved role lane"
            + (outside == null ? "" : $"; first offender {outside.Node.Id} ({outside.Node.Card.Name}), controller {outside.Node.Owner}, seat {outside.Seat}, role {outside.Role}, bounds {outside.Node.Pose.Bounds}, lane {outside.Lane}, territory {outside.Territory}, pose {outside.Node.Pose}, corners {string.Join(";", outside.Node.Pose.Corners)}"));
        var cards = Cards(scene).ToArray();
        var overlap = cards.SelectMany(card => cards.Where(other => other.Owner != card.Owner && card.Pose.Bounds.Intersects(other.Pose.Bounds))
            .Select(other => new { First = card, Second = other })).FirstOrDefault();
        var actionOverlap = cards.FirstOrDefault(card => card.Pose.Bounds.Intersects(SharedTableStudy.ActionArea));
        var controls = TurnControlLayout.For(TableLayout.Hero(3)).Reservations;
        check(cards.All(card => controls.All(bounds => !card.Pose.Bounds.Intersects(bounds))),
            "Study " + label + ": battlefield faces stay clear of the local portrait's phase and priority controls");
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
            Copy("counter-copy") with { Power = 3, Toughness = 3, Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 1) },
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
        var ordered = state with { Decision = new Decision("order-copies", new DecisionContext(), new OrderingPayload(new ChoiceSelection(copies.Take(3).Select((card, index) => new Choice(index, card.Name, card) { CardId = card.VisualId }).ToImmutableArray(), new SelectionRange(0, 0), []))) };
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
        using var app = new TableApplication(TestTableServices.For(engine), profile, initialState: lab.State) { Automated = true };
        app.Initialize();
        app.TestPort.SetTableStudy(true);
        app.TestPort.Accept(ReplaceField(lab.State, 2, [hidden]));
        app.Update(new Microsoft.Xna.Framework.GameTime(TimeSpan.Zero, TimeSpan.Zero), new(), new(), new(1600, 900));
        var frame = app.ReadPresentation();
        var text = frame.Drawing.Commands.Select(command => command switch {
            DrawCommand.Text value => value.Value, DrawCommand.CenterText value => value.Value, _ => null
        }).Where(value => value != null).ToArray();
        check(app.TestPort.TableStudyEnabled && frame.Scene!.BattlefieldCard(2, hidden.VisualId) is { ScanFace: true, Card.FaceDown: true }
                && CardPresentation.Artwork(hidden) == null && text.Contains("Face-down permanent")
                && text.All(value => !value!.Contains("HIDDEN_", StringComparison.Ordinal)),
            "The production study painter masks a hidden printed name, artwork, cost and rules while retaining its public anonymous creature state");
        check(frame.Cards.Where(card => card.Zone == "Battlefield").All(card => {
                var node = frame.Scene!.BattlefieldCard(card.Request.Owner!.Value, card.Id)!;
                var placement = node.Attachment is { } attachment ? frame.Scene.BattlefieldDrawOrder.Single(item => item.Id == attachment.RootId) : node;
                int seat = frame.Scene.Seat(placement.Owner).Index;
                return SharedTableStudy.Lane(seat, BattlefieldLayout.Role(placement.Card, CardPresentation.IsResource)).Contains(card.Rest.Bounds);
            }),
            "The composed study's actual resting card/input poses stay in their host's role lane, including attachments, flying and phased state presentation");
    }

    private static void NameFamilies(GameSnapshot baseline, Action<bool, string> check)
    {
        Card Land(string id) => new() { VisualId = id, CombatId = id, Key = "action:" + id,
            Name = "Forest", Type = "Basic Land", Selectable = true };
        Card[] cards = [Land("land-ready-a"), Land("land-ready-b"),
            Land("land-tapped") with { Tapped = true },
            Land("land-counter") with { Counters = ImmutableDictionary<string, int>.Empty.Add("charge", 1) },
            Land("land-unavailable") with { Selectable = false },
            Land("land-animated") with { Type = "Basic Land Creature", Power = 3, Toughness = 3, Counters = ImmutableDictionary<string, int>.Empty.Add("+1/+1", 3) },
            Land("land-highlighted") with { Highlighted = true },
            Land("hidden-land-a") with { FaceDown = true, Name = "Forest", Selectable = false },
            Land("hidden-land-b") with { FaceDown = true, Name = "Forest", Selectable = false }];
        var state = ReplaceField(baseline, 0, cards);
        var scene = Build(state);
        var forests = Cards(scene).Where(node => node.Owner == 0 && !node.Card.FaceDown).ToArray();
        var family = forests[0].StudyFamily!;
        check(family is { Name: "Forest", Role: "Creatures", TotalCount: 7, CohortCount: 6 }
                && forests.All(node => ReferenceEquals(node.StudyFamily, family))
                && !family.NameBounds.IsEmpty && family.Bounds.Contains(family.NameBounds) && family.Bounds.Contains(family.BodyBounds)
                && family.Cohorts.All(cohort => family.BodyBounds.Contains(cohort.Bounds)
                    && cohort.MemberIds.All(id => ReferenceEquals(scene.BattlefieldCard(0, id)!.StudyFamily, family)))
                && family.Cohorts.SelectMany(cohort => cohort.MemberIds).Order().SequenceEqual(forests.Select(node => node.Id).Order())
                && family.Cohorts.All(cohort => forests.Single(node => node.Id == cohort.FrontCardId).GroupFront),
            "One public-name family contains every Forest, including its animated member, with a reserved shared caption and exact state-cohort occurrence links");
        var ready = family.Cohorts.Single(cohort => cohort.MemberIds.Contains("land-ready-a"));
        check(ready.MemberIds.SequenceEqual(["land-ready-a", "land-ready-b"])
                && ready.FrontCardId == "land-ready-b" && scene.BattlefieldCard(0, ready.FrontCardId)!.Card.Key == "action:land-ready-b"
                && family.Cohorts.Where(cohort => cohort != ready).All(cohort => cohort.MemberIds.Count == 1)
                && family.Cohorts.All(cohort => !family.Cohorts.Any(other => other != cohort && cohort.Bounds.Intersects(other.Bounds))),
            "Tapped, counter-bearing, animated, highlighted and differently available Forests retain distinct visible state faces; routine actions still belong to one exact current front key");
        var hidden = Cards(scene).Where(node => node.Owner == 0 && node.Card.FaceDown).ToArray();
        check(hidden.Length == 2 && hidden.All(node => node.StudyFamily is { Name: "Face-down permanent", TotalCount: 1, CohortCount: 1 })
                && hidden.Select(node => node.StudyFamily!.Id).Distinct().Count() == 2
                && hidden.All(node => node.StudyFamily!.Id != family.Id && CardPresentation.Artwork(node.Card) == null),
            "Hidden same-name faces remain separate anonymous families and never inherit a visible Forest's caption or artwork");

        var refreshed = Build(ReplaceField(state, 0, cards.Select(card => card with { Key = "new-prompt:" + card.VisualId }).ToArray()));
        check(Cards(scene).All(node => refreshed.BattlefieldCard(node.Owner, node.Id)!.Pose == node.Pose)
                && refreshed.BattlefieldCard(0, "land-ready-b")!.Card.Key == "new-prompt:land-ready-b",
            "Refreshing exact decision keys preserves family geometry while each occurrence receives only its new current action key");
        foreach (bool direct in new[] { false, true }) {
            var choice = state with { Decision = direct ? new Decision("direct-land-choice", new DecisionContext(), new OptionsPayload(new ChoiceSelection(cards.Take(2).Select((card, index) => new Choice(index, card.Name, card) { CardId = card.VisualId }).ToImmutableArray(), new SelectionRange(1, 1), []))) : new Decision("land-target", new DecisionContext(), new EntitySelectionPayload(new DecisionReply("Confirm", false), new DecisionReply("Cancel", false), [], null)) };
            var individual = Build(choice);
            var candidates = cards.Take(2).Select(card => individual.BattlefieldCard(0, card.VisualId)!).ToArray();
            check(candidates.All(node => node.GroupFront && node.GroupCount == 1)
                    && !candidates[0].Pose.Bounds.Intersects(candidates[1].Pose.Bounds),
                direct ? "Direct same-name choices preserve each actual candidate as its own visible target"
                    : "Required target selection expands individually eligible same-name instances instead of choosing through a shared front");
        }

        Card Token(int index) => new() { VisualId = "family-token-" + index, Key = "token-action-" + index,
            Name = "Cat", Type = "Token Creature", Power = 2, Toughness = 2 };
        var few = Build(ReplaceField(baseline, 0, Enumerable.Range(0, 2).Select(Token).ToArray()));
        var many = Build(ReplaceField(baseline, 0, Enumerable.Range(0, 200).Select(Token).ToArray()));
        var manyNodes = Cards(many).Where(node => node.Owner == 0).ToArray();
        check(manyNodes.Length == 200 && manyNodes.All(node => node.GroupCount == 200)
                && manyNodes.Count(node => node.GroupFront) == 1
                && manyNodes[0].StudyFamily!.Bounds == few.BattlefieldCard(0, "family-token-0")!.StudyFamily!.Bounds
                && manyNodes[0].StudyFamily!.Cohorts[0].MemberIds.Count == 200
                && manyNodes.All(node => node.Card.Key == "token-action-" + node.Id["family-token-".Length..]),
            "Two or two hundred equivalent copies occupy one bounded fan/count footprint while retaining every actual member and exact action key");

        Card[] lands = [Land("compact-ready") with { Selectable = false }, Land("compact-tapped") with { Selectable = false, Tapped = true }];
        var mixedLands = Build(ReplaceField(baseline, 0, lands));
        var matchingLands = Build(ReplaceField(baseline, 0, lands.Select(card => card with { Tapped = false }).ToArray()));
        var mixedNodes = Cards(mixedLands).Where(node => node.Owner == 0).ToArray();
        var landFamily = mixedNodes[0].StudyFamily!;
        check(landFamily is { Role: "Lands", CohortCount: 2, TotalCount: 2 } && landFamily.NameBounds.Height >= 23
                && mixedNodes.All(node => node.Pose.Bounds.Width <= 57 && node.GroupFront && node.GroupCount == 1)
                && !mixedNodes[0].Pose.Bounds.Intersects(mixedNodes[1].Pose.Bounds)
                && landFamily.Bounds.Width <= 120
                && matchingLands.BattlefieldCard(0, "compact-ready")!.StudyFamily is { CohortCount: 1, TotalCount: 2 }
                && landFamily.Cohorts.All(cohort => cohort.MemberIds.Count == 1
                    && mixedLands.BattlefieldCard(0, cohort.FrontCardId)!.Card.Key == "action:" + cohort.FrontCardId),
            "Tapped and untapped ordinary lands share one bounded turning footprint with compact separate state faces, a real caption band, and each exact action key; "
            + $"family {landFamily.Bounds}, caption {landFamily.NameBounds}, matching {matchingLands.BattlefieldCard(0, "compact-ready")!.StudyFamily!.BodyBounds}; "
            + string.Join("; ", mixedNodes.Select(node => $"{node.Id}: {node.Pose.Bounds}, front {node.GroupFront}, count {node.GroupCount}")));
    }

    private static void FocusedRows(GameSnapshot snapshot, Action<bool, string> check)
    {
        var state = snapshot with { Players = snapshot.Players.Where(player => player.Id is 0 or 2).Select(player => player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with {
                Cards = zone.Cards.Select(card => card with { Key = "focus-action:" + card.VisualId }).ToImmutableArray()
            }).ToImmutableArray()
        }).ToImmutableArray() };
        var scene = TableScene.Build(state, new(-100, -100), null, null, false,
            CardPresentation.IsResource, sharedTableStudy: true);
        var scutes = Cards(scene).Where(node => node.Owner == 2 && node.Card.Name == "Scute Swarm").ToArray();
        var family = scutes[0].StudyFamily!;
        check(family is { TotalCount: 9, CohortCount: 4 } && family.BodyBounds.Width > family.BodyBounds.Height
                && scutes.Count(node => node.GroupFront) == 4
                && scutes.All(node => node.Card.Key == "focus-action:" + node.Id)
                && family.Cohorts.SelectMany(cohort => cohort.MemberIds).Order().SequenceEqual(scutes.Select(node => node.Id).Order()),
            "Two-player rows keep all four Scute Swarm states in a compact horizontal family block without losing copies or exact action keys");
        var families = scene.Seats.SelectMany(seat => seat.Battlefield.SelectMany(cluster => cluster.Cards
            .Select(node => node.StudyFamily!).DistinctBy(item => item.Id).Select(item => new { Seat = seat.Index, Family = item }))).ToArray();
        check(families.All(item => DuelTableStudy.MatchLane(item.Seat, item.Family.Role).Contains(item.Family.Bounds))
                && families.All(item => !families.Any(other => other.Family.Id != item.Family.Id
                    && item.Family.Bounds.Intersects(other.Family.Bounds)))
                && Cards(scene).All(node => node.Pose.Contains(node.Pose.Center.ToPoint())),
            "Two-player row packing reserves every family's caption and body without overlapping another family, lane or actual card target");
    }

    private static GameSnapshot ReplaceField(GameSnapshot state, int owner, Card[] cards) => state with {
        Players = state.Players.Select(player => player.Id != owner ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with { Count = cards.Length, Cards = cards.ToImmutableArray() }).ToImmutableArray()
        }).ToImmutableArray()
    };
    private static IEnumerable<SceneCard> Cards(TableScene scene) => scene.Seats.SelectMany(seat => seat.Battlefield).SelectMany(cluster => cluster.Cards);
    private static TableScene Build(GameSnapshot state) => TableScene.Build(state, new(-100, -100), null, null,
        false, CardPresentation.IsResource, sharedTableStudy: true);
}
