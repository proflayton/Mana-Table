using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

internal static class TokenArtworkChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) }) {
            string directory = Path.Combine(profile, "token-artwork-" + size.X); Directory.CreateDirectory(directory);
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(directory);
            var provider = new TokenCatalog(); var setup = new InteractionScenarioEngine();
            using var app = new TableApplication(new(provider, setup, setup, setup.InitializeAsync) { Tokens = provider }, directory) { Automated = true };
            app.Initialize(); var view = app.TestPort; PresentationFrame frame = null!;
            double now = 0;
            void Tick(Point? at = null, ButtonState left = ButtonState.Released)
            {
                now += .025;
                var physical = RenderViewport.Fit(size, view.DesignHeight).ToPhysical((at ?? new Point(20, 70)).ToVector2() + new Vector2(.5f));
                app.Update(new(TimeSpan.FromSeconds(now), TimeSpan.FromSeconds(.025)),
                    new((int)physical.X, (int)physical.Y, 0, left, default, default, default, default), new(), size);
                frame = app.ReadPresentation();
            }
            void Until(Func<bool> ready, string message)
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while ((frame == null || !ready()) && view.Error.Length == 0 && DateTime.UtcNow < deadline) { Tick(); Thread.Yield(); }
                check(ready() && view.Error.Length == 0, message + (view.Error.Length > 0 ? ": " + view.Error : "")); Tick();
            }
            void Click(string id)
            {
                Until(() => !view.Busy && !view.ActivityOpen && frame.Hits.Any(hit => hit.Id == id), "The current token-artwork control is available: " + id);
                var point = frame.Hits.Single(hit => hit.Id == id).Bounds.Center;
                check(view.HitAt(point)?.Id == id, "The token-artwork card owns its visible pointer area: " + id);
                Tick(point, ButtonState.Pressed); Tick(point);
            }
            Until(() => view.Loaded && !view.Busy && !view.ActivityOpen, "The saved deck opens through production startup");
            Click("edit-deck"); Click("workshop-tokens");
            Until(() => frame.Hits.Any(hit => hit.Id == "token-printing:cat-1"), "The deck's exact token definitions and newest artwork load without a modal");
            check(frame.Observe().Text.Any(text => text.Value.Contains("Newest printing")) && provider.Choices.Count == 0,
                "Newest is an automatic default, not an implicit pinned preference");
            var old = frame.Hits.Single(hit => hit.Id == "token-printing:cat-1");
            Click(old.Id);
            Until(() => !view.Busy && view.DeckDetails?.Revision == 1 && frame.Hits.Any(hit => hit.Id == "token-newest"),
                "Choosing the token face saves this exact printing through the normal deck persistence path");
            check(provider.Choices.Single() == ("cat", "cat-1") && view.DeckDetails!.Entries.Sum(row => row.Quantity) == 100
                    && frame.Observe().Text.Any(text => text.Value == "Deck saved"),
                "Saved acknowledgement stays visible while the token choice leaves playable cards and commander count intact");
            view.Activate(old); Tick();
            check(provider.Choices.Count == 1, "A captured old printing hit cannot write into the next deck revision");
            Click("token-newest");
            Until(() => !view.Busy && view.DeckDetails?.Revision == 2 && frame.Hits.Any(hit => hit.Id == "token-printing:cat-1"), "Use newest clears the pin and reloads the default face");
            check(provider.Choices.Last() == ("cat", null), "Returning to newest persists a reset, including when the currently shown art is already newest");
            provider.GateNextRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Click("token-printings-next");
            Until(() => provider.GatedRead != null, "An older token printing page is deliberately held in flight");
            Click("deck-token:treasure");
            provider.GateNextRead!.SetResult(provider.GatedRead!);
            Until(() => frame.Hits.Any(hit => hit.Id == "token-printing:treasure-0"), "Switching token definitions discards the late previous-token page and loads the current gallery");
            check(!frame.Hits.Any(hit => hit.Id.StartsWith("token-printing:cat-", StringComparison.Ordinal)),
                "Late artwork from another token cannot become an offered choice");
            Click("token-printing:treasure-0");
            Until(() => !view.Busy && view.DeckDetails?.Revision == 3, "A second token definition saves independently");
            check(provider.Choices.Last() == ("treasure", "treasure-0") && setup.Replies.Count == 0,
                "Token artwork changes target their exact definition and never issue a game command");
        }
    }

    private sealed class TokenCatalog : IEngineCatalog, IDeckTokens
    {
        private DeckDetails deck = new("tokens", "Retreat deck", 0,
            [new("Main", "retreat", new() { Name = "Felidar Retreat", Type = "Enchantment" }, 1),
             new("Main", "forest", new() { Name = "Forest", Type = "Basic Land" }, 98),
             new("Commander", "rhys", new() { Name = "Rhys the Redeemed", Type = "Legendary Creature" }, 1)], [new("rhys", [])], "", "");
        internal List<(string Token, string? Printing)> Choices { get; } = [];
        private readonly Dictionary<string, string> preferences = [];
        internal TaskCompletionSource<CatalogPage>? GateNextRead;
        internal CatalogPage? GatedRead;
        private static CatalogCard Printing(string token, int index) => new(token + "-" + index,
            new() { Name = token == "cat" ? "Cat Beast Token" : "Treasure Token", GamePieceType = "token", ArtSet = "tset", Edition = "SET", CollectorNumber = index.ToString(),
                Type = token == "cat" ? "Creature — Cat Beast" : "Artifact — Treasure", Power = token == "cat" ? 2 : null, Toughness = token == "cat" ? 2 : null }, 0, 0, "Tokens", "SET");
        public Task<DeckTokenCatalog> DeckTokensAsync(string id, long revision) => Task.FromResult(new DeckTokenCatalog(id, revision,
            new[] { "cat", "treasure" }.Select(token => {
                string selected = preferences.GetValueOrDefault(token, token + "-0");
                return new DeckTokenAppearance(token, Printing(token, int.Parse(selected.Split('-')[1])), selected, token + "-0", !preferences.ContainsKey(token), false, 6, ["Felidar Retreat"]);
            }).ToImmutableArray()));
        public Task<CatalogPage> TokenPrintingsAsync(string id, long revision, string token, int offset = 0, int limit = 4)
        {
            var page = new CatalogPage(6, offset, Enumerable.Range(offset, Math.Min(limit, 6 - offset)).Select(index => Printing(token, index)).ToImmutableArray());
            if (GateNextRead != null && GatedRead == null) { GatedRead = page; return GateNextRead.Task; }
            return Task.FromResult(page);
        }
        public Task<DeckDetails> SetTokenPrintingAsync(string id, long revision, string token, string? printing)
        {
            if (id != deck.Id || revision != deck.Revision) throw new InvalidOperationException("Stale token choice");
            Choices.Add((token, printing)); if (printing == null) preferences.Remove(token); else preferences[token] = printing;
            return Task.FromResult(deck = deck with { Revision = revision + 1 });
        }
        public Task<ImmutableArray<DeckSummary>> DecksAsync() => Task.FromResult<ImmutableArray<DeckSummary>>([new(deck.Id, deck.Name)]);
        public Task<ImmutableArray<DeckPreset>> PresetsAsync() => Task.FromResult(ImmutableArray<DeckPreset>.Empty);
        public Task<DeckDetails> OpenDeckAsync(string id) => Task.FromResult(deck);
        public Task<CatalogPage> SearchCardsAsync(CardQuery query) => Task.FromResult(new CatalogPage(0, 0, []));
        public Task<DeckDetails> SaveDeckAsync(string id, long revision) => Task.FromResult(deck);
        public Task<DeckSummary> ImportAsync(string name, string text) => throw new NotSupportedException();
        public Task<DeckSummary> ImportPresetAsync(string id) => throw new NotSupportedException();
        public Task<DeckDetails> SetCommandersAsync(string id, long revision, ImmutableArray<string> cards) => throw new NotSupportedException();
        public Task<DeckDetails> CreateDeckAsync(string name) => throw new NotSupportedException();
        public Task<DeckDetails> EditDeckAsync(string id, long revision, ImmutableArray<DeckEdit> edits) => throw new NotSupportedException();
        public Task<DeckDetails> RenameDeckAsync(string id, long revision, string name) => throw new NotSupportedException();
        public Task<DeckDetails> DuplicateDeckAsync(string id, long revision, string name) => throw new NotSupportedException();
        public Task<DeckDetails> UndoDeckAsync(string id, long revision) => throw new NotSupportedException();
        public Task<DeckDetails> RedoDeckAsync(string id, long revision) => throw new NotSupportedException();
        public Task<string> ExportDeckAsync(string id, long revision) => throw new NotSupportedException();
    }
}
