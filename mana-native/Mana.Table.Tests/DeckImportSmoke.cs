using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mana.Contracts;
using Mana.Forge;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

// File and clipboard import both hand their captured text to ImportDeck. This
// exercises that shared native flow with the real bundled rules adapter.
internal static class DeckImportSmoke
{
    internal static void Run(ForgePaths paths, string importFile, Point? size)
    {
        string text = File.ReadAllText(importFile);
        var checks = new List<string>();
        void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
            checks.Add(message); File.AppendAllText(Path.Combine(paths.Profile, "deck-import-progress.log"), message + "\n");
        }
        var rows = Regex.Matches(text, @"(?m)^\s*(\d+)[xX]?\s+[^\r\n]+");
        Check(rows.Count == 95 && rows.Sum(row => int.Parse(row.Groups[1].Value)) == 100, "The annotated export fixture contains 95 card rows and 100 copies");
        var working = paths with { Profile = Path.Combine(paths.Profile, "deck-import-case-" + Guid.NewGuid().ToString("N")) };
        Directory.CreateDirectory(working.Profile);
        string deckId = "", contents = "";
        string[] savedFiles = [];
        const string deckName = "Native annotated Toph import";
        const string invalidCard = "Definitely Not A Real Mana Table Card";
        Task<(string Export, ImmutableArray<DeckSummary> Decks)>? inspected = null;
        static string Contents(DeckDetails deck) => string.Join('\n', deck.Entries.Select(e => $"{e.Section}:{e.Id}:{e.Quantity}").Order(StringComparer.Ordinal));
        static int Copies(DeckDetails deck, string name) => deck.Entries.Where(e => e.Card.Name == name).Sum(e => e.Quantity);
        string[] Files() => Directory.GetFiles(Path.Combine(working.Profile, "decks"), "*.json").Select(file => Path.GetFileName(file)!).Order(StringComparer.Ordinal).ToArray();
        void Scene(Action<Script, TableWindow.Probe> arrange)
        {
            var engine = new ForgeEngine(working);
            try {
                using var game = new TableWindow(TestTableServices.For(engine), working.Profile, null, null, size, artSource: new EmptyArt());
                var script = new Script(game.TestPort); arrange(script, game.TestPort);
                game.Automation = script;  game.Run();
            } finally { engine.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        Scene((s, view) => {
            s.Ready();
            s.Then(() => view.ImportDeck(deckName, text));
            s.Ready();
            s.Then(() => {
                var deck = view.DeckDetails!;
                Check(view.DeckEditor && deck.Name == deckName && !view.DeckConfirmed, "The shared native import flow opens the imported list in the builder without confirming it");
                Check(deck.Entries.Length == 95 && deck.Entries.Sum(e => e.Quantity) == 100, "Import preserves every annotated card entry and all 100 copies");
                Check(deck.Entries.Where(e => e.Section == "Commander").Select(e => (e.Card.Name, e.Quantity)).SequenceEqual([("Toph, the First Metalbender", 1)]) && deck.Entries.Where(e => e.Section == "Main").Sum(e => e.Quantity) == 99, "The inline commander tag selects Toph and leaves the remaining 99 cards in the main deck");
                Check(Copies(deck, "Forest") == 4 && Copies(deck, "Mountain") == 3, "Export quantities ending in x retain all Forest and Mountain copies");
                Check(deck.Problem.Length == 0 && deck.SaveError.Length == 0, "The complete imported Commander deck validates and saves successfully");
                deckId = deck.Id; contents = Contents(deck); savedFiles = Files(); s.Capture("01-imported-user-deck");
            });
            s.Click("deck-done");
            s.Then(() => Check(view.SetupModal.Length == 0 && !view.DeckEditor && !view.DeckConfirmed && view.SelectedDeck?.Id == deckId, "Leaving the imported builder returns to setup with the same saved deck and no forced review"));
            s.Click("change-deck"); s.Click(() => "deck:" + deckId);
            s.Then(() => Check(view.HomeVisible && !view.DeckConfirmed && view.SelectedDeck?.Id == deckId
                && Contents(view.DeckDetails!) == contents, "The real deck picker returns the exact imported list to Home with its quantities and commander intact and no forced review"));
            s.Click("edit-deck"); s.Ready();
            s.Then(() => Check(view.DeckEditor && Contents(view.DeckDetails!) == contents, "Reopening the native builder refreshes the exact imported document"));
            s.Then(() => { s.AllowError = true; view.ImportDeck("Rejected annotated list", text + "\n1x " + invalidCard + " (zzz) 999 [Removal]\n"); });
            s.Until(() => !view.Busy && view.Error.Length > 0);
            s.Then(() => {
                Check(view.ActivityOpen && view.Error.Contains(invalidCard, StringComparison.Ordinal) && view.Error.Contains("Line", StringComparison.OrdinalIgnoreCase), "An unresolved card blocks import with its offending line instead of an empty-paste error");
                Check(view.DeckDetails?.Id == deckId && Contents(view.DeckDetails!) == contents && Files().SequenceEqual(savedFiles), "A failed annotated import preserves the current document and creates no partial saved deck");
            });
            s.Click("activity-dismiss");
            s.Then(() => {
                Check(view.Error.Length == 0 && view.DeckEditor && view.SelectedDeck?.Id == deckId && Contents(view.DeckDetails!) == contents, "Back returns to the unchanged imported deck after the failed request");
                s.AllowError = false;
                inspected = Inspect(view.Services.Catalog, deckId, view.DeckDetails!.Revision);
            });
            s.Until(() => inspected!.IsCompleted);
            s.Then(() => {
                var result = inspected!.GetAwaiter().GetResult();
                Check(result.Export.Contains("4 Forest", StringComparison.Ordinal) && result.Export.Contains("3 Mountain", StringComparison.Ordinal) && result.Export.Contains("Toph, the First Metalbender", StringComparison.Ordinal), "The adapter still has the original active deck after rejecting the bad import");
                Check(result.Decks.Length == 1 && result.Decks[0].Id == deckId, "Only the successful import appears in the saved deck library");
                s.Capture("02-rejected-import-retained-deck");
            });
        });
        Scene((s, view) => {
            s.Ready();
            s.Then(() => {
                var deck = view.DeckDetails!;
                Check(view.SelectedDeck?.Id == deckId && Contents(deck) == contents, "A new native window and Forge process restore the saved import from disk");
                Check(deck.CommanderIds.Length == 1 && Copies(deck, "Forest") == 4 && Copies(deck, "Mountain") == 3 && deck.Problem.Length == 0, "Restart preserves commander selection, basic-land quantities, and deck validity");
            });
            s.Click("review-deck"); s.Click("confirm-edit"); s.Ready();
            s.Then(() => {
                Check(view.DeckEditor && Contents(view.DeckDetails!) == contents, "The persisted import remains editable through native review and builder controls");
                s.Capture("03-restarted-import");
            });
        });
        File.WriteAllText(Path.Combine(paths.Profile, "deck-import-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<(string Export, ImmutableArray<DeckSummary> Decks)> Inspect(IEngineCatalog engine, string id, long revision)
        => (await engine.ExportDeckAsync(id, revision), await engine.DecksAsync());

    private sealed class EmptyArt : ICardArtSource
    {
        public Task<byte[]?> LoadAsync(ArtRequest art, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
        public void Dispose() { }
    }

    private sealed class Script(TableWindow.Probe view) : ITableAutomation
    {
        private readonly Queue<(Action? Action, Func<bool>? Ready)> steps = [];
        private readonly DateTime deadline = DateTime.UtcNow.AddMinutes(4);
        private MouseState mouse;
        private Point target;
        private int mousePhase;
        internal bool AllowError { get; set; }
        public string? ExpectedClick { get; private set; }
        public bool ClickAccepted { get; set; }
        public MouseState ReadMouse()
        {
            if (mousePhase is 1 or 2) {
                bool down = mousePhase++ == 1; var viewport = view.GraphicsDevice.Viewport;
                float scale = Math.Min(viewport.Width / 1600f, viewport.Height / (float)view.DesignHeight);
                mouse = new((int)((viewport.Width - 1600 * scale) / 2 + target.X * scale), (int)((viewport.Height - view.DesignHeight * scale) / 2 + target.Y * scale), 0,
                    down ? ButtonState.Pressed : ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
            }
            return mouse;
        }
        internal void Then(Action action) => steps.Enqueue((action, null));
        internal void Until(Func<bool> ready) => steps.Enqueue((null, ready));
        internal void Ready() => Until(() => view.Loaded && !view.Busy && !view.ActivityOpen);
        internal void Click(string id) => Click(() => id);
        internal void Click(Func<string> id) => Then(() => {
            ExpectedClick = id(); ClickAccepted = false;
            var hit = view.Hits.LastOrDefault(h => h.Id == ExpectedClick) ?? throw new InvalidOperationException("Import smoke is missing " + ExpectedClick);
            target = hit.Bounds.Center; mousePhase = 1;
        });
        internal void Capture(string name)
        {
            using var output = File.Create(Path.Combine(view.Profile, name + ".png")); view.Surface.SaveAsPng(output, view.Surface.Width, view.Surface.Height);
            File.WriteAllText(Path.Combine(view.Profile, name + ".json"), JsonSerializer.Serialize(new { view.DeckDetails, view.SetupModal, view.Error, view.ActivityOpen }, new JsonSerializerOptions { WriteIndented = true }));
        }
        public void Frame(Texture2D surface)
        {
            if (!AllowError && view.Error.Length > 0) { Capture("failure"); throw new InvalidOperationException(view.Error); }
            if (DateTime.UtcNow > deadline) { Capture("timeout"); throw new TimeoutException("Native import smoke timed out with " + steps.Count + " steps remaining"); }
            if (mousePhase is 1 or 2) return;
            if (mousePhase == 3) {
                if (!ClickAccepted) { Capture("failure"); throw new InvalidOperationException("Native import rejected pointer input for " + ExpectedClick); }
                mousePhase = 0; ExpectedClick = null;
            }
            if (!steps.TryPeek(out var step)) { view.Exit(); return; }
            if (step.Ready != null && !step.Ready()) return;
            steps.Dequeue();
            try { step.Action?.Invoke(); } catch { Capture("failure"); throw; }
        }
    }
}
