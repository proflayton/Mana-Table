using Mana.Contracts;
using Mana.Forge;

namespace Mana.Table;

// Runs through the actual native transport and a fresh on-disk engine profile.
// The API's unit fixtures cover provider/network variations separately.
internal static class DeckTuningRegression
{
    internal static async Task<string[]> RunAsync(ForgePaths parent)
    {
        var paths = parent with { Profile = Path.Combine(parent.Profile, "tuning-loop") };
        var checks = new List<string>();
        void Check(bool condition, string description) {
            if (!condition) throw new InvalidOperationException(description);
            checks.Add(description);
        }
        async Task Reject(Func<Task> operation, string description) {
            try { await operation(); }
            catch (InvalidOperationException) { Check(true, description); return; }
            throw new InvalidOperationException(description);
        }
        string deckId, originalVersion, handId, gameId;
        int versionCount;
        await using (var engine = new ForgeEngine(paths)) {
            await engine.InitializeAsync();
            var forest = (await engine.SearchCardsAsync(new("Forest"))).Cards.Single(c => c.Card.Name == "Forest");
            var rhys = (await engine.SearchCardsAsync(new("Rhys the Redeemed"))).Cards.Single(c => c.Card.Name == "Rhys the Redeemed");
            var ring = (await engine.SearchCardsAsync(new("Sol Ring"))).Cards.Single(c => c.Card.Name == "Sol Ring");
            var deck = await engine.CreateDeckAsync("Versioned Commander tuning");
            deck = await engine.EditDeckAsync(deck.Id, deck.Revision, [new("Main", forest.Id, 99), new("Main", rhys.Id, 1)]);
            deck = await engine.SetCommandersAsync(deck.Id, deck.Revision, [rhys.Id]);
            deckId = deck.Id;
            var workshop = await engine.WorkshopAsync(deck.Id, deck.Revision);
            originalVersion = workshop.VersionId;
            Check(originalVersion.Length > 0 && workshop.Versions.Any(v => v.Id == originalVersion && v.MainCount == 99 && v.CommanderCount == 1), "A legal Commander deck receives a durable version with its 99-card library");
            int beforeMetadata = workshop.Versions.Length;
            workshop = await engine.UpdateWorkshopAsync(deck.Id, deck.Revision, new(workshop.MetadataRevision,
                Plan: "Make tokens with Rhys; protect the plan.", Question: "Does this opening hand find green mana?",
                Roles: [new(rhys.Id, ["Tokens", "Payoff"], true, "local")],
                Considering: [new(ring.Id, 1, ["Ramp", "Try later"])]));
            Check(workshop.Plan.Contains("tokens") && workshop.Roles.Any(r => r.CardId == rhys.Id && r.Pinned && r.Roles.Contains("Payoff")), "Plan, overlapping roles, and protected favorite cards save through the native adapter");
            Check(workshop.Considering.Single().Card.Card.Name == "Sol Ring" && (await engine.OpenDeckAsync(deck.Id)).Entries.All(e => e.Id != ring.Id), "Considering cards stay outside the playable Commander deck");
            Check(workshop.VersionId == originalVersion && workshop.Versions.Length == beforeMetadata, "Role, plan, and Considering changes preserve the gameplay version and its evidence");
            await Reject(() => engine.UpdateWorkshopAsync(deck.Id, deck.Revision, new(workshop.MetadataRevision - 1, Plan: "stale")), "A stale metadata save cannot overwrite newer deck annotations");

            // Reopening resets editor revisions by design; all following actions
            // take the current optimistic token instead of persisting it.
            deck = await engine.OpenDeckAsync(deck.Id);
            var hand = await engine.PracticeAsync(deck.Id, deck.Revision, "new", question: workshop.Question);
            handId = hand.TestId;
            Check(hand.VersionId == originalVersion && hand.Hand.Length == 7 && hand.LibraryCount == 92 && hand.Hand.All(c => c.Card.Name == "Forest"), "Opening-hand sampling excludes commanders and locks the tested playable version");
            hand = await engine.PracticeAsync(deck.Id, deck.Revision, "draw");
            Check(hand.Hand.Length == 8 && hand.LibraryCount == 91 && hand.Draws == 1, "Draw-only sampling moves a real card from the sampled library into hand");
            hand = await engine.PracticeAsync(deck.Id, deck.Revision, "keep");
            Check(hand.Status == "completed", "Saving a hand closes its sample honestly without treating it as a game win");
            await Reject(() => engine.PracticeAsync(deck.Id, deck.Revision, "draw"), "A completed hand sample cannot be silently rewritten");
            await engine.SaveTestNoteAsync(deck.Id, handId, "Plenty of green sources; this is deliberately a test fixture.");
            workshop = await engine.WorkshopAsync(deck.Id, deck.Revision);
            deck = await engine.MoveWorkshopCardAsync(deck.Id, deck.Revision, workshop.MetadataRevision, ring.Id, "Main", forest.Id);
            var swapped = await engine.WorkshopAsync(deck.Id, deck.Revision);
            Check(deck.Entries.Sum(e => e.Quantity) == 100 && deck.Entries.Any(e => e.Id == ring.Id) && swapped.Considering.Single().CardId == forest.Id, "A paired swap changes the played list and Considering together without changing deck size");
            deck = await engine.UndoDeckAsync(deck.Id, deck.Revision);
            var undone = await engine.WorkshopAsync(deck.Id, deck.Revision);
            Check(deck.Entries.All(e => e.Id != ring.Id) && undone.Considering.Single().CardId == ring.Id, "Undo restores both halves of a paired swap without losing the candidate");
            deck = await engine.RedoDeckAsync(deck.Id, deck.Revision);
            Check(deck.Entries.Any(e => e.Id == ring.Id) && (await engine.WorkshopAsync(deck.Id, deck.Revision)).Considering.Single().CardId == forest.Id, "Redo restores both the playable swap and its Considering cards");
            deck = await engine.UndoDeckAsync(deck.Id, deck.Revision);
            deck = await engine.EditDeckAsync(deck.Id, deck.Revision, [new("Main", forest.Id, 98), new("Main", ring.Id, 1)]);
            workshop = await engine.WorkshopAsync(deck.Id, deck.Revision);
            Check(workshop.VersionId != originalVersion && workshop.Versions.Length == beforeMetadata + 1, "A paired deck edit creates one new durable version");
            Check((await engine.GetTestsAsync(deck.Id)).Single(t => t.Id == handId).VersionId == originalVersion, "Editing a list never retags historical hand evidence as tested on the newer list");
            deck = await engine.RestoreWorkshopVersionAsync(deck.Id, deck.Revision, originalVersion);
            workshop = await engine.WorkshopAsync(deck.Id, deck.Revision);
            Check(workshop.VersionId == originalVersion && deck.Entries.Single(e => e.Card.Name == "Forest").Quantity == 99 && deck.Entries.All(e => e.Id != ring.Id), "Restoring history recovers exact playable contents and the original evidence identity");
            await Reject(() => engine.RestoreWorkshopVersionAsync(deck.Id, deck.Revision - 1, originalVersion), "Restoring history rejects an obsolete editor revision");

            var setup = await engine.PrepareSoloAsync(deck.Id);
            Check((await engine.WorkshopAsync(deck.Id, deck.Revision)).Revision == deck.Revision, "Preparing opponents preserves the open editor's revision and undo history");
            var test = await engine.StartDeckTestAsync(deck.Id, deck.Revision, [setup.Opponents[0].Id], "Can I reach my first main phase?");
            gameId = test.Id;
            Check(test.Kind == "game" && test.VersionId == originalVersion && test.Opponents.Length == 1, "A real two-player AI test persists its exact deck version and opponent setup before play");
            GameSnapshot? state = null;
            for (int attempt = 0; attempt < 240; attempt++) {
                state = await engine.ObserveAsync();
                if (state is { Players.Length: > 0 }) break;
                await Task.Delay(100);
            }
            Check(state is { Players.Length: 2 }, "The configured two-player test launches a real rules-engine table");
            await engine.ConcedeAsync(state!.Id);
            DeckTest? finished = null;
            for (int attempt = 0; attempt < 240; attempt++) {
                finished = (await engine.GetTestsAsync(deck.Id)).Single(t => t.Id == test.Id);
                if (finished.Status != "running") break;
                await engine.ObserveAsync();
                await Task.Delay(100);
            }
            Check(finished is { Ended: not null } && finished.Status is "conceded" or "completed", "Conceding a real test durably closes its record instead of losing the experiment");
            Check(finished!.Observations.Values.All(v => v >= 0) && !finished.Observations.Keys.Any(k => k.Contains("winrate", StringComparison.OrdinalIgnoreCase)), "Results expose observed counters without inventing a deck win rate");
            await engine.SaveTestNoteAsync(deck.Id, gameId, "Return to tune after conceding.");
            await Reject(() => engine.PreviewDeckSourceAsync("https://example.com/decks/365563"), "Linked imports reject unsupported provider URLs before making a request");
            await Reject(() => engine.ApplyDeckSourceAsync("missing-preview", deck.Id, deck.Revision, "remote"), "An expired or unknown source preview cannot overwrite a deck");
            versionCount = (await engine.WorkshopAsync(deck.Id, deck.Revision)).Versions.Length;
        }
        await using (var reopened = new ForgeEngine(paths)) {
            await reopened.InitializeAsync();
            var deck = await reopened.OpenDeckAsync(deckId);
            var workshop = await reopened.WorkshopAsync(deck.Id, deck.Revision);
            var tests = await reopened.GetTestsAsync(deck.Id);
            Check(workshop.VersionId == originalVersion && workshop.Versions.Length == versionCount && workshop.Plan.Contains("tokens"), "Playable versions, history, and the deck plan survive a complete engine restart");
            Check(workshop.Considering.Single().Card.Card.Name == "Sol Ring" && workshop.Roles.Any(r => r.Pinned), "Considering printings and card annotations survive a complete engine restart");
            Check(tests.Single(t => t.Id == handId).Note.Contains("green") && tests.Single(t => t.Id == gameId).Note.Contains("tune") && tests.All(t => t.VersionId == originalVersion), "Hand and game notes remain attached to their immutable tested version after restart");
            Check(tests.Single(t => t.Id == handId).Hand.Length == 8 && tests.Single(t => t.Id == handId).Hand.All(c => c.Card.Name == "Forest") && tests.Single(t => t.Id == gameId).Hand.Length == 0, "Saved hand inspection survives restart and never substitutes opponent or game hidden cards");
            Check((await reopened.DecksAsync()).Length == 1, "Workshop metadata and test files do not appear as spurious saved decks");
        }
        return checks.ToArray();
    }
}
