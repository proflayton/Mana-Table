using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Presentation;

namespace Mana.Table;

internal static partial class PregameFlowChecks
{
    private static void CheckLobbyDeckNames(string profile, Action<bool, string> check)
    {
        Card pir = new() { Name = "Pir, Imaginative Rascal", Type = "Legendary Creature" };
        Card toothy = new() { Name = "Toothy, Imaginary Friend", Type = "Legendary Creature" };
        Card aiLeader = new() { Name = "Krenko, Mob Boss", Type = "Legendary Creature" };
        var remote = new Seat(1, "A friend", "REMOTE", "Their commander deck", true, false) {
            DeckPreview = new("public-remote-assignment", "Their commander deck", [pir, toothy], null)
        };
        var ai = new Seat(2, "A bot", "AI", "Goblin deck", true, false) {
            DeckPreview = new("public-ai-assignment", "Goblin deck", [aiLeader], null)
        };
        var engine = new PregameEngine { PeerSeats = [remote, ai] };
        var requestedArtwork = new HashSet<string>();
        var resources = new PresentationResources { Card = card => { requestedArtwork.Add(card.Name); return null; } };
        using var run = new Runner(profile, "lobby-deck-names", engine, check, resources: resources);
        bool Text(string value) => run.Frame.Drawing.Commands.Any(command => command switch {
            DrawCommand.Text text => text.Value == value, DrawCommand.CenterText text => text.Value == value, _ => false
        });
        Seat Local() => run.View.Lobby!.Seats.Single(seat => seat.Local);
        bool PeersUnchanged() => run.View.Lobby!.Seats.Single(seat => seat.Index == remote.Index) == remote
            && run.View.Lobby.Seats.Single(seat => seat.Index == ai.Index) == ai
            && Text(remote.Name) && Text(remote.Deck!) && Text(ai.Name) && Text(ai.Deck!);

        run.Click("friends-tab"); run.Click("host");
        run.Until(() => run.View.Lobby?.Mode == "hosting" && !run.View.Busy && !run.View.ActivityOpen,
            "The deck-name journey opens the connected lobby with its assigned saved deck");
        check(Text("Deck a") && Local().DeckPreview?.Name == "Deck a" && PeersUnchanged(),
            "The lobby shows its shared local selection and preserves the remote and AI seats' provider-owned names and decks");
        check(new[] { pir.Name, toothy.Name, aiLeader.Name }.All(name => run.Artwork.Visible.Any(card => card.Name == name)),
            "Both remote partners and the AI commander enter visible image preloading from the public lobby snapshot");
        check(new[] { pir.Name, toothy.Name, aiLeader.Name }.All(requestedArtwork.Contains),
            "Connected lobby portrait drawing requests both public partner faces and the AI commander through the same artwork port");
        var unchangedArtwork = run.Artwork.Visible;
        remote = remote with { Ready = false }; engine.PresentPeer(remote);
        run.Until(() => !run.View.Lobby!.Seats.Single(seat => seat.Index == remote.Index).Ready,
            "A peer's Ready change arrives through ordinary lobby observation");
        check(ReferenceEquals(unchangedArtwork, run.Artwork.Visible),
            "A readiness-only lobby observation reuses its unchanged visible artwork plan");
        Card replacementLeader = new() { Name = "Purphoros, God of the Forge", Type = "Legendary Enchantment Creature" };
        ai = ai with { Deck = "Gods deck", DeckPreview = new("replacement-ai-assignment", "Gods deck", [replacementLeader], null) };
        engine.PresentPeer(ai);
        run.Until(() => run.View.Lobby!.Seats.Single(seat => seat.Index == ai.Index).DeckPreview?.SelectionId == ai.DeckPreview.SelectionId,
            "An authoritative peer-only deck change arrives through ordinary lobby observation");
        check(PeersUnchanged() && run.Artwork.Visible.Any(card => card.Name == replacementLeader.Name)
                && !run.Artwork.Visible.Any(card => card.Name == aiLeader.Name) && Local().DeckPreview?.Name == "Deck a",
            "A peer-only assignment change refreshes visible public artwork without changing the local saved document");
        run.Click("change-deck"); run.Click("deck:b");
        run.Until(() => run.View.SelectedDeck?.Id == "b" && !run.View.Busy && !run.View.ActivityOpen,
            "Changing the local saved deck completes before the player chooses Ready");
        check(Text("Deck b") && !Text("Deck a") && Local() is { Deck: "Deck b", Ready: false, DeckPreview.Name: "Deck b" }
                && run.View.DeckConfirmed && engine.Calls.Count(call => call == "select:b") == 1 && PeersUnchanged(),
            "Changing a saved deck synchronizes its name and public preview before Ready while preserving every other seat");
        requestedArtwork.Clear(); run.Tick();
        check(requestedArtwork.Contains("Omnath, Locus of Mana") && !requestedArtwork.Contains("Rhys the Redeemed")
                && requestedArtwork.Contains(pir.Name) && requestedArtwork.Contains(toothy.Name) && requestedArtwork.Contains(replacementLeader.Name),
            "The shared current assignment supplies the local backdrop and every occupied seat's portrait artwork");
        string assignment = Local().DeckPreview!.SelectionId;

        run.Click("edit-deck");
        run.Until(() => run.View.DeckEditor && !run.View.Busy && !run.View.ActivityOpen,
            "The selected lobby deck opens its actual saved document for renaming");
        check(Local().DeckPreview?.SelectionId == assignment && engine.Calls.Count(call => call == "select:b") == 1,
            "Read-only builder reopening preserves the synchronized assignment when saved bytes have not changed");
        engine.Renaming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        run.View.RenameDeck("Renamed commander deck");
        run.Until(() => run.View.Busy && engine.Calls.Contains("rename:b:Renamed commander deck"),
            "The rename waits for the catalog's persistence acknowledgment");
        check(run.View.DeckDetails!.Name == "Deck b" && run.View.SelectedDeck!.Name == "Deck b" && Local().DeckPreview?.Name == "Deck b",
            "A pending rename retains the previously acknowledged document and shared name");
        engine.Renaming.TrySetResult();
        run.Until(() => !run.View.Busy && run.View.DeckDetails?.Name == "Renamed commander deck",
            "The persisted rename becomes the selected saved document");
        engine.Renaming = null;
        run.Click("deck-done");
        run.Until(() => !run.View.Busy && Local().DeckPreview?.Name == "Renamed commander deck",
            "The acknowledged rename reaches the shared lobby assignment");
        check(Text("Renamed commander deck") && !Text("Deck b") && !Text("Deck a")
                && !Local().Ready && Local().DeckPreview!.SelectionId != assignment && PeersUnchanged(),
            "A saved rename replaces the shared assignment without implicit Ready or changing remote and AI information");

        run.Click("edit-deck");
        run.Until(() => run.View.DeckEditor && !run.View.Busy && !run.View.ActivityOpen,
            "Reopening the saved rename retains its acknowledged deck name");
        engine.FailNextRename = true;
        run.View.RenameDeck("Rejected rename");
        run.Until(() => !run.View.Busy && run.View.Error.Contains("Controlled rename rejection"),
            "A rejected rename reports the catalog failure", allowError: true);
        check(run.View.DeckDetails!.Name == "Renamed commander deck" && run.View.SelectedDeck!.Name == "Renamed commander deck",
            "A failed rename cannot replace the selected document with an unacknowledged name");
        run.ClickAt(run.Hit("deck-done").Bounds.Center);
        run.Until(() => !run.View.DeckEditor && !run.View.Busy && Local().DeckPreview == null,
            "A rejected save clears the previous shared assignment instead of leaving a stale playable deck", allowError: true);
        check(!Text("Rejected rename") && !Local().Ready && !run.View.DeckConfirmed && PeersUnchanged(),
            "A thrown rename rejection publishes no unacknowledged name, cannot Ready, and preserves remote and AI seats");

        using var empty = new Runner(profile, "lobby-without-selected-deck", new PregameEngine { EmptyDeckLibrary = true }, check);
        empty.Click("friends-tab"); empty.Click("host");
        empty.Until(() => empty.View.Lobby?.Mode == "hosting" && !empty.View.Busy && !empty.View.ActivityOpen,
            "An empty local library can open a lobby without assigning a deck");
        empty.Tick();
        var labels = empty.Frame.Drawing.Commands.OfType<DrawCommand.Text>().Select(text => text.Value).ToArray();
        check(empty.View.DeckDetails == null && empty.View.Lobby!.Seats.Single(seat => seat.Local).DeckPreview == null && labels.Contains("Choosing a deck"),
            "An empty local library leaves the shared local seat visibly unassigned");

        var disk = new PregameEngine();
        using var unsaved = new Runner(profile, "lobby-unsaved-rename", disk, check);
        unsaved.Click("friends-tab"); unsaved.Click("host");
        unsaved.Until(() => unsaved.View.Lobby?.Mode == "hosting" && !unsaved.View.Busy && !unsaved.View.ActivityOpen,
            "The failed-save journey starts from an assigned saved deck");
        unsaved.Click("edit-deck");
        unsaved.Until(() => unsaved.View.DeckEditor && !unsaved.View.Busy && !unsaved.View.ActivityOpen,
            "The failed-save journey opens the saved document");
        disk.NextRenameSaveError = "Controlled disk rejection";
        unsaved.View.RenameDeck("Unsaved renamed deck");
        unsaved.Until(() => !unsaved.View.Busy && unsaved.View.DeckDetails?.SaveError.Length > 0,
            "A returned save-error document remains explicitly unsaved", allowError: true);
        unsaved.ClickAt(unsaved.Hit("deck-done").Bounds.Center);
        unsaved.Until(() => !unsaved.View.DeckEditor && !unsaved.View.Busy && unsaved.View.Lobby!.Seats.Single(seat => seat.Local).DeckPreview == null,
            "An unsaved returned rename clears the old shared selection", allowError: true);
        check(!unsaved.Frame.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value == "Unsaved renamed deck")
                && !unsaved.View.DeckConfirmed && !unsaved.View.Lobby!.Seats.Single(seat => seat.Local).Ready,
            "The lobby never advertises an unsaved renamed deck or preserves the former deck's Ready eligibility");
    }

    private sealed partial class PregameEngine
    {
        internal ImmutableArray<Seat> PeerSeats { get; init; } = [];
        internal bool EmptyDeckLibrary { get; init; }
        internal TaskCompletionSource? Renaming;
        internal bool FailNextRename;
        internal string NextRenameSaveError = "";
        private int assignments;
        private DeckDetails? renamedDeck;
        internal void PresentPeer(Seat peer)
        {
            if (lobby == null || peer.Local) throw new InvalidOperationException("No remote pregame seat.");
            lobby = lobby with { Seats = lobby.Seats.Select(seat => seat.Index == peer.Index ? peer : seat).ToImmutableArray(), Revision = lobby.Revision + 1 };
        }
        public async Task<DeckDetails> RenameDeckAsync(string id, long revision, string name)
        {
            var current = SavedDeck(id);
            if (id is not ("a" or "b") || current.Revision != revision)
                throw new InvalidOperationException("Stale controlled rename");
            Calls.Enqueue("rename:" + id + ":" + name);
            if (Renaming != null) await Renaming.Task;
            if (FailNextRename) { FailNextRename = false; throw new IOException("Controlled rename rejection"); }
            renamedDeck = current with { Name = name, Revision = revision + 1, SaveError = NextRenameSaveError,
                LibraryToken = NextRenameSaveError.Length == 0 ? "saved:" + id + ":" + (revision + 1) : current.LibraryToken };
            NextRenameSaveError = "";
            return renamedDeck;
        }
    }
}
