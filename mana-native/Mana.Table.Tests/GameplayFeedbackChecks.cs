using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;

namespace Mana.Table;

internal static class GameplayFeedbackChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var state = PresentationSmoke.Example();
        var presence = new TablePresence(); var presenceActivity = new PublicActivityCursor();
        var tutor = state with { Players = state.Players.Select(p => p.Id == 1 ? p with { Zones = p.Zones.Select(z => z.Name == "Hand"
            ? z with { Count = z.Count + 1 } : z.Name == "Library" ? z with { Count = z.Count - 1 } : z).ToImmutableArray() } : p).ToImmutableArray(),
            Activity = [new(1, state.Turn, state.PhaseKey, "moved", 1, "A card moved from library to hand.", null, null)] };
        presence.Observe(presenceActivity.Observe(state, tutor), 1);
        check(presence.Cues.All(c => c.Kind != SeatCueKind.Draw), "A tutor's matching library/hand deltas never invent a draw animation");
        var legacyTransfer = tutor with { Activity = [new(1, state.Turn, state.PhaseKey, "draw", 1, "A card moved from library to hand.", null, null)] };
        var legacyPresence = new TablePresence(); var legacyPresenceActivity = new PublicActivityCursor(); var legacyFeedback = new ActionFeedback(); var legacyFeedbackActivity = new PublicActivityCursor();
        legacyPresence.Observe(legacyPresenceActivity.Observe(state, legacyTransfer), 1); legacyFeedback.Observe(legacyFeedbackActivity.Observe(state, legacyTransfer), 1);
        check(legacyPresence.Cues.Count == 0 && legacyFeedback.LastPublicOutcome == null,
            "An older protocol-compatible host's uncounted library-to-hand event never invents a draw animation or asserted draw outcome");
        var wheel = tutor with { Activity = [.. tutor.Activity, new(2, state.Turn, state.PhaseKey, "draw", 1, "Opponent drew cards.", null, null) { Count = 8 }] };
        presence.Observe(presenceActivity.Observe(tutor, wheel), 2);
        check(presence.Cues.Single(c => c.Kind == SeatCueKind.Draw).Amount == 8 && presence.PendingDraws(1, 2) == 7,
            "An actual eight-card draw keeps its full public count with a bounded anonymous visual batch despite unchanged final zones");
        presence.Observe(presenceActivity.Observe(tutor, wheel), 2.3);
        check(presence.Cues.Single(c => c.Kind == SeatCueKind.Draw).Started == 2,
            "Reobserving an already consumed event cannot replay or prolong its draw cue");
        var feedback = new ActionFeedback(); var feedbackActivity = new PublicActivityCursor();
        var noResolution = wheel with { Activity = [.. wheel.Activity, new(3, state.Turn, state.PhaseKey, "resolved", 1, "Spell did not resolve.", null, "Spell") { Outcome = "notResolved" }] };
        feedback.Observe(feedbackActivity.Observe(wheel, noResolution), 3);
        check(feedback.Last is { Heading: "DID NOT RESOLVE", Outcome: "notResolved" },
            "An engine-reported non-resolution gets a non-resolution heading rather than JUST RESOLVED");
        var success = noResolution with { Activity = [.. noResolution.Activity, new(4, state.Turn, state.PhaseKey, "resolved", 1, "Spell resolved.", null, "Spell") { Outcome = "resolved" }] };
        feedback.Observe(feedbackActivity.Observe(noResolution, success), 4);
        check(feedback.Last is { Heading: "JUST RESOLVED", Outcome: "resolved" }, "An actual successful resolution retains its affirmative heading");
        var legacy = success with { Activity = [.. success.Activity, new(5, state.Turn, state.PhaseKey, "resolved", 1, "Legacy outcome.", null, "Spell")] };
        feedback.Observe(feedbackActivity.Observe(success, legacy), 5);
        check(feedback.Last?.Heading == "REPORTED OUTCOME", "An older engine without typed outcome is not guessed to have resolved successfully");
        var events = legacy with { Activity = [.. legacy.Activity,
            new(6, state.Turn, state.PhaseKey, "draw", 1, "Opponent drew cards.", null, null) { Count = 9 },
            new(7, state.Turn, state.PhaseKey, "shuffle", 1, "Opponent shuffled their library.", null, null) { Count = 1 }] };
        feedback.Observe(feedbackActivity.Observe(legacy, events), 6);
        check(feedback.LastPublicOutcome is { Messages.Length: 2 } outcome && outcome.Messages.Any(m => m.Contains("9 cards"))
            && outcome.Messages.All(m => m.Contains("Seat 2")) && outcome.Messages.Any(m => m.Contains("shuffled")),
            "Actual public draw and shuffle events retain exact batch counts and acting seat without hidden card identity");
        feedback.Observe(feedbackActivity.Observe(legacy, events), 7);
        check(feedback.LastPublicOutcome?.Started == 6, "Repeated event observation does not restart public outcome feedback");
        var decline = events with { Revision = events.Revision + 1 };
        feedback.Observe(feedbackActivity.Observe(events, decline), 8);
        check(feedback.LastPublicOutcome?.Started == 6, "A search decline or unchanged snapshot cannot invent a shuffle event");
        feedback.Observe(feedbackActivity.Observe(events, events with { Id = "new-game" }), 9);
        check(feedback.Last == null && feedback.LastPublicOutcome == null, "A new game cannot inherit earlier resolution or public outcome cues");
        CheckResolutionDraws(check, state);
        CheckPlayerStates(check, state);
        CheckEffectArtwork(check);
    }

    private static void CheckEffectArtwork(Action<bool, string> check)
    {
        var effect = new Card { Name = "Start Your Engines!", GamePieceType = "effect", Type = "Effect", Text = "Speed 1" };
        check(Mana.Magic.CardPresentation.Artwork(effect) == null
            && Mana.Magic.CardPresentation.Artwork(effect with { Name = "An unrelated status", ArtName = "A real card" }) == null,
            "Engine effect markers never request printed-card artwork, including collisions with real sorcery names");
        var printed = effect with { Name = "Start Your Engines", GamePieceType = "card", Type = "Sorcery" };
        check(Mana.Magic.CardPresentation.Artwork(printed) is { Name: "Start Your Engines", Face: "front" }
            && Mana.Magic.CardPresentation.Artwork(printed with { FaceDown = true }) == null,
            "The actual printed sorcery retains its artwork while hidden objects do not request an image");
        var face = printed with { ArtName = "A printed double-faced card", ArtFace = "back" };
        check(Mana.Magic.CardPresentation.Artwork(face) is { Name: "A printed double-faced card", Face: "back" },
            "Ordinary alternate faces retain their explicit printed artwork identity");
        var source = new ObservedArt();
        using var art = new CardArt(source);
        check(art.Get(effect) == null && art.Get(effect with { Name = "Another marker", ArtFace = "back" }) == null
            && !source.Requested.Task.Wait(TimeSpan.FromMilliseconds(100)),
            "The actual texture loader declines effect objects before consulting its cache or image source");
    }

    private sealed class ObservedArt : ICardArtSource
    {
        internal readonly TaskCompletionSource<ArtRequest> Requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<byte[]?> LoadAsync(ArtRequest request, CancellationToken cancellationToken)
        {
            Requested.TrySetResult(request);
            return Task.FromResult<byte[]?>(null);
        }
        public void Dispose() { }
    }

    private static void CheckPlayerStates(Action<bool, string> check, GameSnapshot state)
    {
        var feedback = new ActionFeedback(); var feedbackActivity = new PublicActivityCursor();
        var started = state with { Activity = [
            new(1, state.Turn, state.PhaseKey, "playerState", state.ViewerId, "Your Speed is now 1/4.", null, null) {
                Detail = "Speed increases once on each of your turns when an opponent loses life." },
            new(2, state.Turn, state.PhaseKey, "resolved", state.ViewerId, "A creature resolved.", null, null) { Outcome = "resolved" }] };
        feedback.Observe(feedbackActivity.Observe(state, started), 20);
        check(feedback.LastPublicOutcome is { Messages: ["Your Speed is now 1/4."] }
            && feedback.Last is { Changes.Length: 0 },
            "An unattributed player-state change remains visible without assigning it to a resolution sharing the poll");
        feedback.Observe(feedbackActivity.Observe(started, started with { Revision = started.Revision + 1 }), 21);
        check(feedback.LastPublicOutcome?.Started == 20 && feedback.Last?.Started == 20,
            "Repeated snapshots cannot replay or prolong a player-state transition");
        var unknown = started with { Activity = [.. started.Activity,
            new(3, state.Turn, state.PhaseKey, "playerState", 1, "Opponent gained a new public status.", null, null)] };
        feedback.Observe(feedbackActivity.Observe(started, unknown), 22);
        check(feedback.LastPublicOutcome is { Messages: ["Opponent gained a new public status."], Started: 22 },
            "A newly reported public mechanic uses the shared feedback path without a mechanic-specific client case");
        var unrelated = unknown with { Activity = [.. unknown.Activity,
            new(4, state.Turn, state.PhaseKey, "resolved", state.ViewerId, "Another ability resolved.", null, null) { Outcome = "resolved" }] };
        feedback.Observe(feedbackActivity.Observe(unknown, unrelated), 23);
        check(feedback.Last is { Changes.Length: 0 } && feedback.LastPublicOutcome?.Started == 22,
            "A later unrelated resolution does not inherit an earlier player-state transition");
        var noEvent = unrelated with { Players = unrelated.Players.Select(p => p.Id == state.ViewerId
            ? p with { States = [new("speed", "Speed", "progress", 4, 4, "Max speed.")] } : p).ToImmutableArray() };
        feedback.Observe(feedbackActivity.Observe(unrelated, noEvent), 24);
        check(feedback.LastPublicOutcome?.Started == 22,
            "A changed snapshot alone does not invent or attribute a public state-change event");
        check(Mana.Magic.PresentationPacing.ShouldPresent(unrelated, noEvent)
            && Mana.Magic.PresentationPacing.ShouldPresent(state, state with { States = [new("day-night", "Night", "status", null, null, "It is night.")] }),
            "Generic player and table state changes pause automatic passing without inventing an attributed effect");
        var anonymous = new Card { CombatId = "anonymous-pacing", FaceDown = true, Name = "Face-down card", Type = "Creature", Power = 2, Toughness = 2 };
        GameSnapshot Anonymous(Card card) => state with { Players = state.Players.Select(player => player.Id != state.ViewerId ? player : player with {
            Zones = player.Zones.Select(zone => zone.Name != "Battlefield" ? zone : zone with { Cards = [card], Count = 1 }).ToImmutableArray()
        }).ToImmutableArray() };
        check(Mana.Magic.PresentationPacing.ShouldPresent(Anonymous(anonymous), Anonymous(anonymous with { PhasedOut = true }))
            && Mana.Magic.PresentationPacing.ShouldPresent(Anonymous(anonymous), Anonymous(anonymous with { States = [new("ward", "Ward {3}", "ability", null, null, "A public effective ability.", "ward")] })),
            "Anonymous public occurrences and typed effective abilities retain their presentation beat using public combat identity");
        feedback.Observe(feedbackActivity.Observe(noEvent, noEvent with { Id = "another-table" }), 25);
        check(feedback.Last == null && feedback.LastPublicOutcome == null,
            "Player-state cues from a previous table cannot leak into a new game");
    }

    private static void CheckResolutionDraws(Action<bool, string> check, GameSnapshot state)
    {
        var feedback = new ActionFeedback(); var feedbackActivity = new PublicActivityCursor();
        var draw = state with { Activity = [
            new(1, state.Turn, state.PhaseKey, "moved", state.ViewerId, "Toothy moved from battlefield to hand.", null, null),
            new(2, state.Turn, state.PhaseKey, "moved", state.ViewerId, "You moved a card from library to hand.", null, null),
            new(3, state.Turn, state.PhaseKey, "draw", state.ViewerId, "You drew a card.", null, null) { Count = 1 },
            new(4, state.Turn, state.PhaseKey, "moved", state.ViewerId, "You moved a card from library to hand.", null, null),
            new(5, state.Turn, state.PhaseKey, "draw", state.ViewerId, "You drew a card.", null, null) { Count = 1 },
            new(6, state.Turn, state.PhaseKey, "moved", state.ViewerId, "You moved a card from library to hand.", null, null),
            new(7, state.Turn, state.PhaseKey, "draw", state.ViewerId, "You drew a card.", null, null) { Count = 1 },
            new(8, state.Turn, state.PhaseKey, "resolved", state.ViewerId, "Toothy's ability resolved.", null, "Toothy") {
                Outcome = "resolved", Detail = "Draw a card for each +1/+1 counter on Toothy." }] };
        feedback.Observe(feedbackActivity.Observe(state, draw), 10);
        check(feedback.LastPublicOutcome?.Started == feedback.Last?.Started,
            "A resolution and its draw events can be published in the same observed frame");
        check(feedback.Last is { Title: "Toothy's ability resolved.", Heading: "JUST RESOLVED" } resolved
            && resolved.Detail.Contains("+1/+1 counter") && resolved.Changes.Length == 0
            && feedback.LastPublicOutcome is { Messages: ["You drew 3 cards."] },
            "Legacy resolution text and exact public draw totals remain separate without an execution relationship");
        feedback.Observe(feedbackActivity.Observe(draw, draw with { Revision = draw.Revision + 1 }), 11);
        check(feedback.Last?.Started == 10 && feedback.LastPublicOutcome?.Started == 10
            && feedback.Last.Changes.Length == 0,
            "Reobserving a resolution does not replay, prolong or add its draw total again");
        var unrelated = draw with { Activity = [.. draw.Activity,
            new(9, state.Turn, state.PhaseKey, "resolved", state.ViewerId, "Sol Ring's ability resolved.", null, "Sol Ring") { Outcome = "resolved" }] };
        feedback.Observe(feedbackActivity.Observe(draw, unrelated), 12);
        check(feedback.Last is { Title: "Sol Ring's ability resolved.", Changes.Length: 0 }
            && feedback.LastPublicOutcome?.Started == 10,
            "An unrelated later resolution never inherits a cached draw total from a previous outcome");
        var spell = unrelated with { Activity = [.. unrelated.Activity,
            new(10, state.Turn, state.PhaseKey, "draw", 1, "Opponent drew cards.", null, null) { Count = 2 },
            new(11, state.Turn, state.PhaseKey, "resolved", 1, "Divination resolved.", null, "Divination") { Outcome = "resolved" }] };
        feedback.Observe(feedbackActivity.Observe(unrelated, spell), 13);
        check(feedback.Last is { Title: "Divination resolved.", Changes.Length: 0 }
            && feedback.LastPublicOutcome!.Messages.Single().Contains("Seat 2 drew 2 cards."),
            "An unrelated resolution cannot claim a legacy draw sharing its poll; the public total and seat remain visible");
        var tutor = spell with { Activity = [.. spell.Activity,
            new(12, state.Turn, state.PhaseKey, "moved", state.ViewerId, "A card moved from library to hand.", null, null),
            new(13, state.Turn, state.PhaseKey, "resolved", state.ViewerId, "Search spell resolved.", null, "Search spell") { Outcome = "resolved" }] };
        feedback.Observe(feedbackActivity.Observe(spell, tutor), 14);
        check(feedback.Last is { Changes.Length: 0 }
            && feedback.LastPublicOutcome?.Started == 13,
            "A library-to-hand move is never inferred to be a draw or given an older spell's counted draw result");
    }
}
