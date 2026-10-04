using Mana.Contracts;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Mana.Table.Tests")]

namespace Mana.Table;

// The application exposes input/frame observations; scenarios and assertions live in the test assembly.
internal interface ITableAutomation
{
    MouseState ReadMouse();
    KeyboardState? ReadKeyboard() => null;
    string? ExpectedClick { get; }
    bool ClickAccepted { get; set; }
    void Frame(Texture2D surface);
}
internal sealed partial class TableGame
{
    internal ITableAutomation? Automation { get; set; }
    internal Probe TestPort => new(this);
    internal sealed class Probe(TableGame game)
    {
        internal string Profile => game.profile;
        internal GameSnapshot? Match => game.match;
        internal string Error => game.error;
        internal bool Loaded => game.loaded;
        internal bool Busy => game.busy;
        internal bool Polling => game.polling;
        internal IReadOnlyList<DeckPreset> Presets => game.presets;
        internal DeckSummary? SelectedDeck => game.selectedDeck;
        internal DeckDetails? DeckDetails => game.selectedDeckDetails;
        internal bool DeckEditor => game.deckEditor;
        internal bool DeckConfirmed => game.DeckConfirmed;
        internal Mana.Contracts.TableConversation? Conversation => game.conversation.State;
        internal int ChatUnread => game.conversation.Unread;
        internal bool ChatOpen => game.chatOpen;
        internal bool SocialSending => game.socialSending;
        internal string ChatDraft => game.chatDraft;
        internal string ChatError => game.chatError;
        internal string PlayerName => game.preferences.PlayerName;
        internal bool ChatVisible(TableMessage message) => game.conversation.Visible(message);
        internal void TypeText(string text) { foreach (char c in text) game.ReceiveText(c); }
        internal string SetupModal => game.setupModal;
        internal DeckEntry? ReviewedCard => game.reviewCard;
        internal CatalogCard? DeckSelection => game.DeckSelection;
        internal void TypeSetupText(string text) { foreach (char c in text) game.SetupText(c); }
        internal CatalogPage? Catalog => game.catalogPage;
        internal bool CatalogBusy => game.catalogInFlight || game.catalogPending;
        internal void TypeDeckText(string text) { foreach (char c in text) game.DeckText(c); }
        internal void DeckQuantity(int quantity) => game.DeckQuantity(quantity);
        internal void RenameDeck(string name) => game.RenameDeck(name);
        internal void DuplicateDeck(string name) => game.DuplicateDeck(name);
        internal void EditPreset(string id) => game.ImportDeck(() => game.engine.ImportPresetAsync(id), true);
        internal Lobby? Lobby => game.lobby;
        internal void ImportDeck(string name, string text) => game.ImportDeck(() => game.engine.ImportAsync(name, text), true);
        internal SoloSetup? SoloSetup => game.soloSetup;
        internal int[] SoloChoices => game.soloChoices;
        internal bool LocalMatch => game.localMatch;
        internal bool Automatic => game.automatic;
        internal bool Held => game.held;
        internal Mana.Renderer.ResolutionCue? LastResolution => game.actionFeedback.Last;
        internal void SetAutomatic(bool value) => game.SetAutomatic(value);
        internal List<int> Selections => game.selections;
        internal string Overlay => game.overlay;
        internal string? ZoneName => game.zoneName;
        internal List<Hit> Hits => game.hits;
        internal string Scope => game.Scope;
        internal GraphicsDevice GraphicsDevice => game.GraphicsDevice;
        internal int DesignHeight => game.DesignHeight;
        internal Hit? HitAt(Point p) => game.HitAt(p);
        internal bool CurrentHit(Hit hit) => game.CurrentHit(hit);
        internal IEnumerable<Card> AllCards() => game.AllCards();
        internal void Exit() => game.Exit();
        internal bool SoloTab => game.soloTab;
        internal ICardEngine Engine => game.engine;
        internal string? SelectedCombat => game.selectedCombat;
        internal void Accept(GameSnapshot? state) => game.Accept(state);
        internal string? InspectedStack => game.inspectedStack;
        internal int ZonePage => game.zonePage;
        internal Hit? Pressed { get => game.pressed; set => game.pressed = value; }
        internal MouseState PreviousMouse { get => game.previousMouse; set => game.previousMouse = value; }
        internal Texture2D Surface => game.surface;
        internal double Now => game.now;
        internal Rectangle? HoverPreview => game.hoverPreview;
        internal Card? Hovered => game.hovered;
        internal int CardPaintCount(string id) => game.cardPaintCounts.GetValueOrDefault(id);
        internal Texture2D? Art(Card card) => game.art.Get(card);
        internal Mana.Renderer.Pose? DisplayedPose(string id) => game.motion.TryGet(id, out var pose) ? pose : null;
    }
}
