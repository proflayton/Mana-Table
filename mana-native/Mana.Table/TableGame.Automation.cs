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
        internal bool StartupVisible => game.StartupVisible;
        internal bool ActivityOpen => game.ActivityOpen;
        internal bool InlineActivityVisible => game.InlineActivityVisible;
        internal string ActivityTitle => game.activityTitle;
        internal string ActivityDetail => game.activityDetail;
        internal string ActivityStage => game.activityStage.ToString() ?? "";
        internal bool StartupFailed => game.startupFailed;
        internal IReadOnlyList<DeckPreset> Presets => game.presets;
        internal DeckSummary? SelectedDeck => game.selectedDeck;
        internal DeckDetails? DeckDetails => game.selectedDeckDetails;
        internal bool DeckEditor => game.deckEditor;
        internal bool AnalysisOpen => game.analysisOpen;
        internal DeckAnalysisState? Analysis => game.analysisState;
        internal bool AnalysisCurrent => game.AnalysisCurrent;
        internal string[] AnalysisVisibleRecommendationIds => game.analysisState is { } a ? game.VisibleRecommendations(a).Select(r => r.Id).ToArray() : [];
        internal bool UpdatesOpen => game.updatesOpen;
        internal bool UpdateChecking => game.updateChecking;
        internal bool UpdateDownloading => game.updateDownloading;
        internal bool UpdateValidating => game.updateValidating;
        internal bool UpdateInstallerLaunched => game.updateInstallerLaunched;
        internal bool UpdateCheckOnStartup => game.updatePreferences.CheckOnStartup;
        internal bool AutomaticUpdateCheckAllowed => game.AutomaticUpdateCheckAllowed;
        internal string UpdateError => game.updateError;
        internal string UpdateNotice => game.updateNotice;
        internal string UpdateInstallBlocker => game.UpdateInstallBlocker();
        internal long UpdateBytes => game.updateBytes;
        internal Mana.Client.AppRelease? AvailableUpdate => game.availableUpdate;
        internal Mana.Client.PreparedUpdate? PreparedUpdate => game.preparedUpdate;
        internal void OpenAppUpdates() => game.OpenAppUpdates();
        internal void CheckAppUpdates() => game.CheckAppUpdates();
        internal void Post(Action action) => game.updates.Enqueue(action);
        internal void SetAutomation(ITableAutomation? value) => game.Automation = value;
        internal void SetUpdateGuardState(bool connected = false, string saveError = "", bool text = false) {
            game.lobby = connected ? new("hosting", "Controlled table", null, false, null, false, "", null, [], []) : null;
            game.selectedDeckDetails = new("update-test", "Update test", 0, [], [], "", saveError);
            game.textFocus = text; game.nextPoll = double.MaxValue;
        }
        internal string DeckFilter => game.deckFilter;
        internal void SetAnalysisProfile(AnalysisProfile profile) => game.SaveAnalysisProfile(profile);
        internal void SaveAnalysisDecision(string id, string decision, string note) => game.RecordAnalysis(id, decision, note);
        internal Rectangle? DeckIssueBounds => game.deckIssueBounds;
        internal IReadOnlySet<string> RenderedDeckIssueCodes => game.renderedDeckIssueCodes;
        internal IReadOnlySet<string> RenderedDeckIssueCardIds => game.renderedDeckIssueCardIds;
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
        internal WorkshopState? Workshop => game.workshop;
        internal DeckVersionView? WorkshopVersion => game.workshopVersionView;
        internal void ShowSourcePreview(DeckSourcePreview preview) {
            game.workshopSourcePreview = preview; game.workshopSourceImport = false; game.workshopSourceChoice = "";
            game.workshopDialog = "source-preview"; game.WorkshopDialogPage = 0;
        }
        internal bool WorkshopCurrent => game.WorkshopCurrent;
        internal HandPractice? WorkshopHand => game.workshopHand;
        internal DeckTest[] WorkshopTests => game.workshopTests;
        internal string WorkshopTab => game.workshopTab;
        internal string WorkshopDialog => game.workshopDialog;
        internal string WorkshopError => game.workshopError;
        internal void SetWorkshopPlan(string plan, string question) => game.SaveWorkshopMetadata(new(Plan: plan, Question: question), "Test plan saved.");
        internal void SaveWorkshopNote(string testId, string note) => game.SaveWorkshopTestNote(game.workshopTests.First(t => t.Id == testId), note);
        internal void ConcedeWorkshop() => game.ReturnToWorkshop();
        internal Lobby? Lobby => game.lobby;
        internal bool LobbyAiSeatControlsAvailable => game.CanManageLobbySeats;
        internal void PresentLobby(Lobby state, bool pausePolling) { game.lobby = state; game.nextPoll = pausePolling ? double.MaxValue : game.now; }
        internal void ImportDeck(string name, string text) => game.ImportDeck(() => game.engine.ImportAsync(name, text), true);
        internal SoloSetup? SoloSetup => game.soloSetup;
        internal int[] SoloChoices => game.soloChoices;
        internal bool LocalMatch => game.localMatch;
        internal bool NeedsRecovery => game.session.NeedsRecovery;
        internal float TextScale => game.preferences.TextScale;
        internal Rectangle DecisionTextBounds => game.decisionTextBounds;
        internal Rectangle DecisionPanelBounds => game.decisionPanelBounds;
        internal string DecisionText => game.decisionText;
        internal bool DecisionTextOverflow => game.decisionTextOverflow;
        internal string HelpPageText => game.helpPageText;
        internal IReadOnlyList<BattlefieldLabelPaint> BattlefieldLabels(string id) => game.battlefieldLabels.TryGetValue(id, out var labels) ? labels : [];
        internal Mana.Renderer.TableScene? TableScene => game.tableScene;
        internal Rectangle? BattlefieldRoleLabel(int player, string kind) => game.battlefieldRoleLabels.TryGetValue((player, kind), out var bounds) ? bounds : null;
        internal void CaptureWindow(string path) => game.windowCapture = path;
        internal void ImportFilePath(string path) => game.ImportFilePath(path);
        internal void SetConnectedTable(Lobby state) { game.localMatch = false; game.lobby = state; }
        internal bool Automatic => game.automatic;
        internal bool Held => game.held;
        internal PublicCounterBadge? CounterBadge(int player) => game.publicCounterBadges.TryGetValue(player, out var badge) ? badge : null;
        internal PublicResourcePaint? ManaPoolBadge(int player) => game.manaPoolBadges.TryGetValue(player, out var badge) ? badge : null;
        internal PublicResourcePaint? ManaPoolDetail => game.overlay == "mana" ? game.manaPoolDetail : null;
        internal AttachmentBadgePaint? AttachmentBadge(string id) => game.attachmentBadges.TryGetValue(id, out var badge) ? badge : null;
        internal IReadOnlyList<AttachmentConnection> AttachmentConnections => game.attachmentConnections;
        internal bool WelcomeOpen => game.welcomeOpen;
        internal bool TableGuideOpen => game.tableGuideOpen;
        internal bool ExperienceOpen => game.ExperienceOpen;
        internal IReadOnlySet<string> LearnedControls => game.experience.LearnedControls;
        internal Mana.Magic.ControlCue? GuidanceCue => game.guidanceCue;
        internal Rectangle? GuidanceBounds => game.guidanceBounds;
        internal void DismissWelcome() => game.DismissWelcome();
        internal void OpenTableGuide() => game.OpenTableGuide();
        internal void SetHeld(bool value) { game.held = value; game.priority.Reset(); }
        internal Mana.Renderer.ResolutionCue? LastResolution => game.actionFeedback.Last;
        internal void SetAutomatic(bool value) => game.SetAutomatic(value);
        internal List<int> Selections => game.selections;
        internal IReadOnlyDictionary<int, ChoiceOrderBadge> ChoiceOrderBadges => game.choiceOrderBadges;
        internal IReadOnlyList<int> AllocationAmounts => game.allocations;
        internal int AllocationDetail => game.allocationDetail;
        internal string? AllocationProblem => game.match?.Decision is { Kind: "allocate" } decision ? Mana.Magic.AllocationGuide.Problem(decision, game.allocations) : null;
        internal string Overlay => game.overlay;
        internal string? ZoneName => game.zoneName;
        internal IReadOnlyList<Hit> Hits => game.hits.Published;
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
        internal int CardPaintCount(string id) => game.cardPresentation.CompositionCount(id);
        internal Texture2D? Art(Card card) => game.art.Get(card);
        internal Mana.Renderer.Pose? DisplayedPose(string id) => game.cardPresentation.TryGetPose(id, out var pose) ? pose : null;
    }
}
