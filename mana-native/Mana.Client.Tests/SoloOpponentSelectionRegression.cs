using System.Collections.Immutable;
using System.Text.Json;
using Mana.Client;
using Mana.Contracts;

internal static class SoloOpponentSelectionRegression
{
    internal static void Run(Action<bool, string> check)
    {
        AiOpponent Deck(string id, char composition, string? name = null) => new(id, name ?? id, "Controlled legal catalog choice") {
            CompositionId = new(composition, 64)
        };
        SoloSetup Setup(params AiOpponent[] decks) => new("human", "Shared name", decks.ToImmutableArray()) { MaxPlayers = 4 };
        string[] Ids(SoloOpponentSelection selection) => selection.Choices.Select(choice => choice.OpponentId).ToArray();
        string[] Compositions(SoloSetup setup, SoloOpponentSelection selection) => Ids(selection)
            .Select(id => setup.Opponents.Single(deck => deck.Id == id).CompositionId).ToArray();
        bool Rejected(SoloSetup setup, int count = 3) {
            try { SoloOpponentSelection.Prepare(setup, SoloOpponentSelection.Empty, 19, count); return false; }
            catch (InvalidOperationException) { return true; }
        }

        var catalog = Setup(Deck("a", 'a'), Deck("b", 'b'), Deck("c", 'c'), Deck("d", 'd'), Deck("e", 'e'));
        var reversed = catalog with { Opponents = catalog.Opponents.Reverse().ToImmutableArray() };
        var chosen = SoloOpponentSelection.Prepare(catalog, SoloOpponentSelection.Empty, 123);
        check(Ids(chosen).SequenceEqual(Ids(SoloOpponentSelection.Prepare(reversed, SoloOpponentSelection.Empty, 123))),
            "A seeded fallback pod depends on canonical deck identities rather than catalog or saved-date order");
        check(chosen.Choices.All(choice => !choice.Explicit) && Compositions(catalog, chosen).Distinct().Count() == 3,
            "Automatic fallback seats use three distinct available compositions and carry no manual intent");
        var varied = Enumerable.Range(0, 32).Select(seed => string.Join("|", Ids(SoloOpponentSelection.Prepare(catalog,
            SoloOpponentSelection.Empty, seed)))).Distinct().Count();
        check(varied > 1, "New fallback preparation seeds can produce different legal pods");

        var aliased = catalog with { Opponents = [.. catalog.Opponents, Deck("z-import-a", 'a'), Deck("z-foil-a", 'a')] };
        check(Enumerable.Range(0, 16).All(seed => Compositions(catalog, SoloOpponentSelection.Prepare(catalog, SoloOpponentSelection.Empty, seed))
                .SequenceEqual(Compositions(aliased, SoloOpponentSelection.Prepare(aliased, SoloOpponentSelection.Empty, seed)))),
            "Importing or reprinting the same composition cannot multiply its chance of occupying an automatic seat");
        var named = Setup(Deck("same-name-different-content", 'b', "Shared name"));
        check(Ids(SoloOpponentSelection.Prepare(named, SoloOpponentSelection.Empty, 0, 1)).Single() == "same-name-different-content",
            "A deck-name collision is not treated as evidence that an AI deck mirrors the human's content");

        var provided = catalog with { DefaultOpponentIds = ["c", "a", "e"] };
        var authoritative = SoloOpponentSelection.Prepare(provided, SoloOpponentSelection.Empty, 9);
        check(Ids(authoritative).SequenceEqual(provided.DefaultOpponentIds),
            "Current provider-composed default seats take precedence over the legacy fallback shuffle");
        var refresh = provided with { Opponents = reversed.Opponents, DefaultOpponentIds = ["b", "d", "a"] };
        var retained = SoloOpponentSelection.Prepare(refresh, authoritative, 200);
        check(Ids(retained).SequenceEqual(Ids(authoritative)) && retained.Indices(refresh)
                .Select(index => refresh.Opponents[index].Id).SequenceEqual(Ids(authoritative)),
            "A reviewed roster survives refreshed defaults, new seeds and catalog reordering through exact opaque IDs");
        check(Ids(authoritative).SequenceEqual(["c", "a", "e"]), "Refreshing a pod does not mutate its previously captured immutable selection");

        var manual = authoritative.Choose(provided, 1, "d");
        check(Ids(manual).SequenceEqual(["c", "d", "e"]) && manual.Choices.Count(choice => choice.Explicit) == 1,
            "Choosing one AI deck pins exactly that seat and leaves the other reviewed choices intact");
        var retired = manual.ClearAutomatic();
        check(retired.Choices[1] is { OpponentId: "d", Explicit: true }
                && retired.Choices.Where((_, index) => index != 1).All(choice => choice is { OpponentId: "", Explicit: false }),
            "Ending preparation retires automatic choices while retaining only deliberate seat preferences");
        var next = SoloOpponentSelection.Prepare(refresh, retired, 8);
        check(Ids(next).SequenceEqual(["b", "d", "a"]) && next.Choices[1].Explicit && next.Choices.Count(choice => choice.Explicit) == 1,
            "A new preparation fills around a retained manual pin using current provider defaults");
        var deliberateDuplicates = authoritative.Choose(provided, 0, "a").Choose(provided, 1, "a");
        check(Ids(SoloOpponentSelection.Prepare(refresh, deliberateDuplicates.ClearAutomatic(), 7)).Take(2).SequenceEqual(["a", "a"]),
            "Deliberate duplicate or mirror preferences are preserved instead of silently repaired by automatic composition");
        var workshop = SoloOpponentSelection.ExplicitIds(["e", "b", "c"]);
        check(Ids(workshop.ClearAutomatic()).SequenceEqual(["e", "b", "c"]) && workshop.Choices.All(choice => choice.Explicit),
            "An existing explicit workshop opponent list survives retirement without being reclassified as automatic");

        var versioned = Setup(Deck("deck:committed:old", 'a'), Deck("other", 'b'), Deck("third", 'c'));
        var old = SoloOpponentSelection.Prepare(versioned, SoloOpponentSelection.Empty, 4).Choose(versioned, 0, "deck:committed:old");
        var newer = versioned with { Opponents = [Deck("deck:committed:new", 'd'), Deck("other", 'b'), Deck("third", 'c')],
            DefaultOpponentIds = ["deck:committed:new", "other", "third"] };
        var refreshed = SoloOpponentSelection.Prepare(newer, old, 4);
        check(!Ids(refreshed).Contains("deck:committed:old") && !refreshed.Choices[0].Explicit,
            "A retired committed deck handle cannot survive as a pin or authorize a future start");
        bool staleRejected;
        try { old.Indices(newer); staleRejected = false; } catch (InvalidOperationException) { staleRejected = true; }
        check(staleRejected, "A stale reviewed roster cannot be converted to the indices of a replacement catalog");

        foreach (int pool in new[] { 1, 2 }) {
            var small = catalog with { Opponents = catalog.Opponents.Take(pool).ToImmutableArray(), MaxPlayers = 6 };
            var filled = SoloOpponentSelection.Prepare(small, SoloOpponentSelection.Empty, 3, 5);
            var counts = Ids(filled).GroupBy(id => id).Select(group => group.Count()).ToArray();
            check(filled.Choices.Length == 5 && counts.Max() - counts.Min() <= 1,
                $"A {pool}-composition catalog fills requested seats with balanced repeats after variety runs out");
        }

        var legacy = JsonSerializer.Deserialize<SoloSetup>("""
            {"DeckId":"legacy-human","DeckName":"Shared name","Opponents":[
              {"Id":"legacy-a","Name":"Shared name","Description":""},
              {"Id":"legacy-b","Name":"Other","Description":""},
              {"Id":"legacy-c","Name":"Third","Description":""}],"MaxPlayers":4}
            """)!;
        check(legacy.DefaultOpponentIds.IsEmpty && legacy.Opponents.All(deck => deck.CompositionId.Length == 0)
                && Ids(SoloOpponentSelection.Prepare(legacy, SoloOpponentSelection.Empty, 42)).Distinct().Count() == 3,
            "An older provider without composition metadata or default seats still yields a varied current-ID fallback pod");
        check(Ids(SoloOpponentSelection.Prepare(legacy with { Opponents = legacy.Opponents.Reverse().ToImmutableArray() },
                SoloOpponentSelection.Empty, 42)).SequenceEqual(Ids(SoloOpponentSelection.Prepare(legacy, SoloOpponentSelection.Empty, 42))),
            "Legacy fallback order is canonical even when names collide or the provider reorders its catalog");

        check(Rejected(catalog with { DefaultOpponentIds = ["retired"] })
                && Rejected(catalog with { DefaultOpponentIds = [null!] })
                && Rejected(catalog with { DefaultOpponentIds = ["a", "b", "c", "d"] }),
            "Unknown, null or oversized provider default references cannot enter a prepared table");
        check(Rejected(catalog with { Opponents = [catalog.Opponents[0], catalog.Opponents[0]] })
                && Rejected(catalog with { Opponents = [Deck("bad", 'a') with { CompositionId = "bad\nidentity" }] })
                && Rejected(catalog with { Opponents = [Deck("long", 'a') with { CompositionId = new string('a', 129) }] })
                && Rejected(catalog with { Opponents = [Deck("null", 'a') with { CompositionId = null! }] }),
            "Duplicate handles and malformed semantic identities fail before any default can be assigned");
        var otherProvider = Setup(Deck("provider-a", 'a') with { CompositionId = "DeckFamily:one" },
            Deck("provider-b", 'b') with { CompositionId = "DeckFamily:two" });
        check(SoloOpponentSelection.Prepare(otherProvider, SoloOpponentSelection.Empty, 7, 2).Choices.Length == 2,
            "Semantic identities remain opaque provider data rather than coupling selection to Forge's hash algorithm");
        check(Rejected(catalog with { Opponents = [] }) && Rejected(catalog with { MaxPlayers = 1 })
                && Rejected(catalog, 0) && Rejected(catalog, 6),
            "An empty catalog or invalid requested table size fails explicitly rather than inventing an opponent");
        bool badSeat, badChoice;
        try { authoritative.Choose(provided, 3, "a"); badSeat = false; } catch (InvalidOperationException) { badSeat = true; }
        try { authoritative.Choose(provided, 0, "retired"); badChoice = false; } catch (InvalidOperationException) { badChoice = true; }
        check(badSeat && badChoice && Ids(authoritative).SequenceEqual(["c", "a", "e"]),
            "Invalid seat or retired-deck choices cannot mutate the prepared roster");
    }
}
