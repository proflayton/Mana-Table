using Mana.Contracts;
using Mana.Magic;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Text.Json;
using Point = Microsoft.Xna.Framework.Point;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

// Real Forge storage/analysis, real input, and read-only history across an actual deck edit.
internal sealed class DeckExperienceSmoke(TableWindow.Probe view) : ITableAutomation
{
    private readonly DateTime deadline = DateTime.UtcNow.AddSeconds(120);
    private readonly List<string> checks = [];
    private int stage, mousePhase, next;
    private Point mouse;
    private string recommendation = "", version = "", sample = "";
    private string experimentQuestion = "";
    private string sourceBefore = "", sourceVersion = "", unchangedSourceCard = "";
    private long sourceMetadataRevision;
    private DeckSourcePreview? sourcePreview;
    private long revision;
    private string sampledHand = "", sampledSetup = "";
    private string TableSetupControls() => string.Join(";", view.Hits.Where(hit => hit.Id.StartsWith("workshop-players:", StringComparison.Ordinal)
        || hit.Id.StartsWith("workshop-opponent-", StringComparison.Ordinal) || hit.Id is "workshop-prepare" or "workshop-start-game")
        .OrderBy(hit => hit.Id).Select(hit => hit.Id + ":" + hit.Bounds));
    private DeckDetails? removedDeck;
    private DeckLibraryDeletion? removedReceipt;
    private Task<IReadOnlyList<DeckLibraryDeletion>>? removedRead;
    private readonly Dictionary<string, byte[]> savedEvidence = [];
    private string independentBefore = "";
    public string? ExpectedClick { get; private set; }
    public bool ClickAccepted { get; set; }
    private DeckDetails Deck => view.DeckDetails!;
    private void Check(bool passed, string description)
    {
        File.AppendAllText(Path.Combine(view.Profile, "deck-experience-progress.log"), $"{view.Now:F3} {stage}: {passed} {description}\n");
        if (!passed) { Capture("failure"); throw new InvalidOperationException("Deck experience stage " + stage + ": " + description); }
        checks.Add(description);
    }
    private void Capture(string name)
    {
        view.CaptureWindow(Path.Combine(view.Profile, "deck-experience-" + name + "-window.png"));
        using var output = File.Create(Path.Combine(view.Profile, "deck-experience-" + name + ".png"));
        view.Surface.SaveAsPng(output, view.Surface.Width, view.Surface.Height);
        File.WriteAllText(Path.Combine(view.Profile, "deck-experience-" + name + ".json"), JsonSerializer.Serialize(new {
            view.DeckDetails, view.Workshop, view.WorkshopTests, view.Analysis, view.WorkshopVersion, view.WorkshopDialog,
            view.SavedDecks, view.DeletedDecks, view.SelectedDeck, view.DeckEditor, view.SetupModal,
            ControlledSourcePreview = sourcePreview, RemovedReceipt = removedReceipt
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private void Click(string id, int target)
    {
        var hit = view.Hits.LastOrDefault(h => h.Id == id) ?? throw new InvalidOperationException("Missing deck experience control " + id + " at " + stage);
        mouse = SmokePointer.Hit(view, hit); ExpectedClick = id; ClickAccepted = false; mousePhase = 1; next = target;
    }
    private DeckSourcePreview SourceFixture()
    {
        var forest = Deck.Entries.Single(e => e.Card.Name == "Forest");
        var removed = Deck.Entries.First(e => e.Section == "Main" && e.Card.Name != "Forest");
        var option = Deck.Entries.Single(e => e.Card.Name == "Lifecrafter's Bestiary");
        unchangedSourceCard = Deck.Entries.First(e => e.Section == "Main" && e.Id != forest.Id && e.Id != removed.Id && e.Id != option.Id).Card.Name;
        DeckSourceCard Row(DeckEntry entry, int quantity, string section, bool included) => new(entry.Id,
            new(entry.Id, entry.Card, entry.ManaValue, entry.ColorIdentity, section, entry.Edition), quantity, section, [], included);
        var cards = Deck.Entries.Where(e => e.Id != removed.Id).Select(e => Row(e,
            e.Quantity + (e.Id == forest.Id ? 2 : 0), e.Section, true)).Append(Row(option, 1, "Considering", false)).ToArray();
        // The token is deliberately never registered with Forge: an accidental
        // early apply fails the test, and no HTTP/provider behavior is simulated.
        return new("unregistered-ui-review-" + Guid.NewGuid(), "https://archidekt.com/decks/123", "Controlled source review", Deck.Format,
            cards, [], [], [
                new("Main\nForest", "Forest", "Main", forest.Quantity - 1, forest.Quantity, forest.Quantity + 2, true),
                new("Main\n" + removed.Card.Name, removed.Card.Name, "Main", removed.Quantity, removed.Quantity, 0, false),
                new("Considering\n" + option.Card.Name, option.Card.Name, "Considering", 0, 0, 1, false)
            ], true);
    }
    private void SourceUnchanged(string action) => Check(JsonSerializer.Serialize(Deck) == sourceBefore
        && view.Workshop!.VersionId == sourceVersion && view.Workshop.MetadataRevision == sourceMetadataRevision,
        action + " leaves the deck ID, revision, cards, saved version and source metadata unchanged before explicit confirmation");
    private void PreservedLibraryEvidence() => Check(savedEvidence.All(file => File.Exists(file.Key)
        && File.ReadAllBytes(file.Key).SequenceEqual(file.Value)),
        "Undo restores the exact saved list and preserves its versions, analysis decisions, question and sampled-hand evidence");
    public MouseState ReadMouse()
    {
        bool down = mousePhase == 1; if (mousePhase is 1 or 2) mousePhase++;
        return new(mouse.X, mouse.Y, 0, down ? ButtonState.Pressed : ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    }
    public void Frame(Texture2D surface)
    {
        if (DateTime.UtcNow > deadline) { Capture("failure"); throw new TimeoutException("Deck experience did not finish at " + stage); }
        if (view.Error.Length > 0 || view.WorkshopError.Length > 0) throw new InvalidOperationException(view.Error + " " + view.WorkshopError);
        if (mousePhase is 1 or 2) return;
        if (mousePhase == 3) { Check(ClickAccepted, "Pointer accepted " + ExpectedClick); mousePhase = 0; ExpectedClick = null; stage = next; }
        if (!view.Loaded || view.Busy || view.ActivityOpen || view.DeckEditor && !view.WorkshopCurrent || view.AnalysisOpen && !view.AnalysisCurrent) return;
        switch (stage) {
            case 0: view.ImportDeck("Deck learning loop", AnalysisRegression.List); stage++; break;
            case 1:
                Check(Deck is { Problem.Length: 0, StructureValid: true, Eligibility: "bundled-rules" }, "The playable list has separate structural and bundled eligibility results");
                version = view.Workshop!.VersionId; revision = Deck.Revision; Click("deck-analysis", 2); break;
            case 2:
                view.SetAnalysisProfile(view.Analysis!.Report.Profile with { Strategy = "artifact-recursion", Pace = "fast" }); stage++; break;
            case 3: Click("analysis-tab:2", 4); break;
            case 4:
                recommendation = view.Analysis!.Report.Recommendations[0].Id; Click("analysis-keep:0", 5); break;
            case 5:
                Check(DeckExperience.LatestDecision(view.Analysis!, recommendation)?.Decision == "keep", "Keep is saved for this playable fingerprint");
                Check(!view.AnalysisVisibleRecommendationIds.Contains(recommendation), "Kept advice leaves the undecided action list");
                Click("analysis-reviewed", 6); break;
            case 6:
                Check(view.Hits.Any(h => h.Id == "analysis-reopen:0"), "Reviewed advice remains visible with an explicit reopen action");
                Capture("01-reviewed"); Click("analysis-reopen:0", 7); break;
            case 7: Click("analysis-reviewed", 8); break;
            case 8: Click("analysis-dismiss:0", 9); break;
            case 9:
                Check(DeckExperience.LatestDecision(view.Analysis!, recommendation)?.Decision == "dismiss" && view.Analysis!.History.Count(h => h.RecommendationId == recommendation) == 3,
                    "Dismiss supersedes the reopened decision while keeping the full history");
                Check(Deck.Revision == revision && Deck.Entries.All(e => e.Card.Name != "Ichor Wellspring"), "Recommendation decisions leave playable cards and editor revision unchanged");
                Click("analysis-tab:0", 91); break;
            case 91:
                experimentQuestion = DeckTuning.FromFinding(view.Analysis!.Report, view.Analysis.Report.Findings[0]).Question;
                Click("analysis-details:0", 92); break;
            case 92:
                Check(view.Hits.Any(hit => hit.Id == "analysis-test-check"), "An actionable finding offers a test question beside its evidence");
                Click("analysis-test-check", 93); break;
            case 93:
                Check(view.Workshop!.Question == experimentQuestion && view.Workshop.VersionId == version && Deck.Revision == revision,
                    "Testing a finding saves its question and keeps the playable cards, revision and version unchanged");
                Capture("01b-finding-experiment"); Click("workshop-hand-new", 11); break;
            case 10: Click("workshop-hand-new", 11); break;
            case 11:
                sample = view.WorkshopHand!.TestId; sampledHand = JsonSerializer.Serialize(view.WorkshopHand); sampledSetup = TableSetupControls();
                Click("workshop-hand-card:0", 110); break;
            case 110:
                Check(view.WorkshopTab == "test" && view.WorkshopDialog.Length == 0 && JsonSerializer.Serialize(view.WorkshopHand) == sampledHand
                    && Deck.Revision == revision && view.Workshop!.VersionId == version && view.Workshop.Question == experimentQuestion,
                    "Reading a sampled card keeps the exact hand, question, editor revision and playable version in Test");
                var readingBounds = view.Hits.Single(hit => hit.Id == "workspace-card-reading").Bounds;
                Check(view.Hits.Any(hit => hit.Id == "workshop-hand-card:1")
                    && view.Hits.Where(hit => hit.Bounds.Intersects(readingBounds)).All(hit => hit.Id is "workspace-card-reading" or "workspace-card-flip"),
                    "The intact read-only sample card uses the setup area while adjacent cards remain clickable and no play or edit action is invented");
                Capture("01c-hand-reading"); Click("workshop-hand-card:1", 111); break;
            case 111:
                Check(view.WorkshopTab == "test" && JsonSerializer.Serialize(view.WorkshopHand) == sampledHand,
                    "Reading the next sampled card requires no tab change or library search and leaves the sample untouched");
                Click("workspace-card-reading", 112); break;
            case 112:
                Check(view.Hits.All(hit => hit.Id != "workspace-card-reading") && TableSetupControls() == sampledSetup
                    && JsonSerializer.Serialize(view.WorkshopHand) == sampledHand,
                    "Closing local card reading restores the exact prior AI setup controls beside the unchanged hand");
                Click("workshop-hand-keep", 12); break;
            case 12: Click("workshop-results", 13); break;
            case 13:
                Check(view.WorkshopTests.Any(t => t.Id == sample && t.VersionId == version && t.Question == experimentQuestion),
                    "A real sample retains both the analysis question and exact playable version");
                Click("workshop-view-tested", 14); break;
            case 14:
                Check(view.WorkshopDialog == "version-view" && view.WorkshopVersion!.Entries.Sum(e => e.Quantity) == 100, "View tested deck opens the complete saved list without restoring it");
                Capture("02-saved-list"); Click("workshop-version-next", 141); break;
            case 141:
                Check(view.WorkshopDialog == "version-view" && view.Hits.Any(h => h.Id == "workshop-version-prev"),
                    "The saved-list modal retains its own second page over the one-page Results view");
                Capture("02b-saved-list-page2"); Click("workshop-version-card:8", 142); break;
            case 142:
                Check(view.WorkshopDialog == "saved-card", "A later saved-list occurrence opens its actual read-only inspector");
                Capture("02c-saved-card"); Click("workshop-dialog-close", 143); break;
            case 143:
                Check(view.WorkshopDialog == "version-view" && view.Hits.Any(h => h.Id == "workshop-version-card:8")
                    && view.Hits.Any(h => h.Id == "workshop-version-prev"), "Returning from card details preserves the saved-list page");
                Click("workshop-view-diff", 15); break;
            case 15:
                Check(view.WorkshopVersion!.Differences.Length == 0 && Deck.Revision == revision, "An unchanged deck comparison is read only");
                Click("workshop-dialog-close", 16); break;
            case 16: Click("workshop-build", 17); break;
            case 17: Click("deck-main", 18); break;
            case 18:
                Click("deck-card:Main:" + Deck.Entries.Single(e => e.Card.Name == "Forest").Id, 19); break;
            case 19: Click("deck-remove", 20); break;
            case 20:
                Check(view.Workshop!.VersionId != version, "A genuine quantity edit creates a different playable version");
                Check(view.WorkshopTests.Single(test => test.Id == sample).Question == experimentQuestion,
                    "Tuning the playable deck preserves the earlier test's original question and evidence");
                revision = Deck.Revision; Click("workshop-results", 21); break;
            case 21: Click("workshop-view-tested", 22); break;
            case 22: Click("workshop-view-diff", 23); break;
            case 23:
                Check(view.WorkshopVersion!.Differences.Any(d => d.Card == "Forest" && d.Saved == 91 && d.Current == 90), "Comparison names the actual saved-to-current card change");
                Check(Deck.Revision == revision && view.WorkshopVersion.Entries.Single(e => e.Card.Name == "Forest").Quantity == 91,
                    "Inspecting an earlier test preserves both lists and the current undo history");
                Capture("03-compared"); Click("workshop-dialog-close", 24); break;
            case 24:
                Check(view.TextScale > 1.29f, "Source review and saved-list acceptance use the 130% text setting");
                sourceBefore = JsonSerializer.Serialize(Deck); sourceVersion = view.Workshop!.VersionId;
                sourceMetadataRevision = view.Workshop.MetadataRevision; sourcePreview = SourceFixture();
                view.ShowSourcePreview(sourcePreview); stage++; break;
            case 25:
                Check(view.WorkshopDialog == "source-preview" && !view.Hits.Any(h => h.Id == "workshop-source-merge"),
                    "An actual conflicting source review disables merging while offering explicit whole-list choices");
                Capture("04-source-review"); Click("workshop-source-keep-local", 26); break;
            case 26:
                Check(view.WorkshopDialog == "source-confirm" && view.Hits.Any(h => h.Id == "workshop-source-confirm-apply"),
                    "Keep local opens a separate baseline-and-quantity confirmation instead of applying immediately");
                SourceUnchanged("Keep local"); Capture("05-keep-local"); Click("workshop-source-confirm-cancel", 27); break;
            case 27: Click("workshop-source-use-remote", 28); break;
            case 28:
                Check(view.WorkshopDialog == "source-confirm", "Use source also requires explicit whole-list confirmation");
                SourceUnchanged("Use source"); Capture("06-use-source"); Click("workshop-source-confirm-cancel", 29); break;
            case 29: Click("workshop-source-copy", 30); break;
            case 30:
                var copyRows = DeckExperience.SourceRows(sourcePreview!, "copy", true);
                Check(view.WorkshopDialog == "source-confirm" && copyRows.Any(r => r.Card == unchangedSourceCard)
                    && sourcePreview!.Changes.All(c => c.Card != unchangedSourceCard) && copyRows.Any(r => r.Section == "Considering" && r.After == 1),
                    "Source-copy confirmation contains unchanged playable rows and the source Considering list, not merely the change subset");
                SourceUnchanged("Save source copy"); Capture("07-source-copy"); Click("workshop-source-confirm-next", 31); break;
            case 31:
                Check(view.WorkshopDialog == "source-confirm" && view.Hits.Any(h => h.Id == "workshop-source-confirm-prev"),
                    "The full source-copy list is reachable through real scoped page controls");
                SourceUnchanged("Paging the source copy"); Capture("08-source-copy-remaining"); Click("workshop-source-confirm-cancel", 32); break;
            case 32:
                Check(view.WorkshopDialog == "source-preview", "Backing out retains the review without accepting a source baseline");
                SourceUnchanged("Canceling source confirmation"); Click("workshop-dialog-close", 33); break;
            case 33:
                SourceUnchanged("Closing source review");
                Check(view.Services.Library is IDeckLibrary { SupportsDeckLibrary: true } && Deck.LibraryToken.Length > 0,
                    "The real Forge adapter negotiates reversible saved-deck operations and an exact saved-content token");
                Click("workshop-build", 34); break;
            case 34:
                removedDeck = Deck;
                foreach (string relative in new[] { Deck.Id + ".json", "workshop/" + Deck.Id + ".json", "workshop-tests/" + sample + ".json" }) {
                    string path = Path.Combine(view.Profile, "decks", relative); savedEvidence.Add(path, File.ReadAllBytes(path));
                }
                Capture("09-before-delete"); Click("deck-delete:" + Deck.Id, 35); break;
            case 35:
                Check(!view.DeckEditor && view.DeckDetails == null && view.SelectedDeck == null && view.SetupModal == "decks"
                    && view.SavedDecks.All(deck => deck.Id != removedDeck!.Id),
                    "The real Delete gesture removes the saved tile and closes its editor without keeping a selected or confirmed list");
                removedReceipt = view.DeletedDecks.Single(deck => deck.DeckId == removedDeck!.Id);
                Check(removedReceipt.Name == removedDeck!.Name && view.Hits.Any(hit => hit.Id == "deck-restore:" + removedReceipt.RestoreToken)
                    && !view.Hits.Any(hit => hit.Id == "deck:" + removedDeck.Id),
                    "The deleted list keeps its complete name beside one visible Undo action");
                Capture("10-deleted-notice"); Click("picker-presets", 36); break;
            case 36:
                Check(!view.Hits.Any(hit => hit.Id.StartsWith("deck-delete:") || hit.Id.StartsWith("deck-restore:")),
                    "Bundled precons remain separate from saved-list deletion controls");
                Click("picker-deleted", 37); break;
            case 37:
                removedRead = view.Services.Library!.DeletedDecksAsync(); view.RefreshDeckLibrary(); stage++; break;
            case 38:
                if (!removedRead!.IsCompleted) return;
                Check(removedRead.GetAwaiter().GetResult().Any(deck => deck == removedReceipt)
                    && view.Hits.Any(hit => hit.Id == "deck-restore:" + removedReceipt!.RestoreToken),
                    "The Deleted tab and a fresh real-backend observation retain the same durable Undo receipt");
                Capture("11-deleted-library"); Click("deck-restore:" + removedReceipt!.RestoreToken, 39); break;
            case 39:
                Check(view.SavedDecks.Any(deck => deck.Id == removedDeck!.Id && deck.LibraryToken == removedDeck.LibraryToken)
                    && view.DeletedDecks.All(deck => deck.DeckId != removedDeck!.Id)
                    && view.SelectedDeck == null && view.DeckDetails == null && !view.DeckEditor,
                    "Undo returns the same saved tile and content token without implicitly selecting or opening it");
                PreservedLibraryEvidence(); Capture("12-restored-unselected"); Click("deck:" + removedDeck!.Id, 40); break;
            case 40:
                Check(view.HomeVisible && view.SelectedDeck?.Id == removedDeck!.Id && !view.DeckConfirmed,
                    "Choosing the restored tile returns its exact saved document to Home without a review or confirmation gate");
                Click("edit-deck", 41); break;
            case 41:
                Check(Deck.Id == removedDeck!.Id && Deck.Name == removedDeck.Name && Deck.LibraryToken == removedDeck.LibraryToken
                    && JsonSerializer.Serialize(Deck.Entries) == JsonSerializer.Serialize(removedDeck.Entries)
                    && view.Workshop!.VersionId == sourceVersion && view.Workshop.Question == experimentQuestion
                    && view.WorkshopTests.Any(test => test.Id == sample && test.VersionId == version),
                    "Reopening the restored list retains its exact cards, current version, tuning question and earlier sample");
                Capture("13-restored-editor");
                view.ImportDeck("Independent saved list", AnalysisRegression.List); stage++; break;
            case 42:
                Check(Deck.Id != removedDeck!.Id, "An independent imported document becomes the current editor");
                independentBefore = JsonSerializer.Serialize(Deck); Click("deck-done", 43); break;
            case 43: Click("change-deck", 44); break;
            case 44: Click("picker-saved", 45); break;
            case 45: Click("deck-delete:" + removedDeck!.Id, 46); break;
            case 46:
                Check(JsonSerializer.Serialize(Deck) == independentBefore && view.SelectedDeck?.Id == Deck.Id
                    && view.SavedDecks.All(deck => deck.Id != removedDeck!.Id),
                    "Deleting another saved tile preserves the current independent document and revision");
                var laterReceipt = view.DeletedDecks.Single(deck => deck.DeckId == removedDeck!.Id);
                Check(laterReceipt.RestoreToken != removedReceipt!.RestoreToken, "A later deletion receives a new scoped Undo token");
                removedReceipt = laterReceipt;
                Capture("14-unrelated-deleted"); Click("picker-deleted", 47); break;
            case 47:
                Capture("15-unrelated-undo"); Click("deck-restore:" + removedReceipt!.RestoreToken, 48); break;
            case 48:
                Check(JsonSerializer.Serialize(Deck) == independentBefore && view.SelectedDeck?.Id == Deck.Id
                    && view.SavedDecks.Any(deck => deck.Id == removedDeck!.Id)
                    && view.DeletedDecks.All(deck => deck.DeckId != removedDeck!.Id),
                    "Undoing another saved tile preserves the independent selection and its exact editor revision");
                PreservedLibraryEvidence(); Capture("16-library-restored");
                File.WriteAllText(Path.Combine(view.Profile, "deck-experience-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
                view.Exit(); stage++; break;
        }
    }
}
