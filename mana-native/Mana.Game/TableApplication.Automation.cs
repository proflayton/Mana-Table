using System.Collections.Immutable;
using Mana.Contracts;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Point = Microsoft.Xna.Framework.Point;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Mana.Table.Tests")]

namespace Mana.Table;

internal sealed partial class TableApplication
{
    internal Probe TestPort => new(this);
    internal class Probe(TableApplication game)
    {
        internal Mana.Table.Presentation.PresentationFrame? Frame => game.Frame;
        internal bool Activate(Hit hit) => game.ActivateHit(hit);
        internal bool SubmitCurrent(DecisionCommand command) => game.Send(command);
        internal Mana.Renderer.BoardView BoardView => game.boardView;
        internal bool FollowTurnEnabled => game.followTurnEnabled;
        internal bool BoardViewLimitedByLayout => game.boardViewMotion.LimitedByLayout;
        internal void SetTurnFollow(bool enabled) => game.SetTurnFollow(enabled);
        internal void SetTableStudy(bool enabled) => game.SetTableStudy(enabled);
        internal bool TableStudyEnabled => game.TableStudyEnabled;
        internal int? StudyFocusOwner => game.StudyFocusActive ? game.studyFocusOwner : null;
        internal IReadOnlyList<Mana.Renderer.EffectChangeGroup> PublicChanges => game.matchPresentation.Changes;
        internal Rectangle? ChangeCalloutBounds => game.changeCalloutBounds;
        internal IReadOnlySet<string> HighlightedChangeTargets => game.highlightedChangeTargets;
        internal string? HighlightedChangeSource => game.highlightedChangeSource;
        internal Mana.Renderer.EffectTransitionFrame EffectTransitions => game.effectTransitionFrame;
        internal IReadOnlyList<CounterReceiptPaint> CounterReceipts => game.counterReceiptPaint;
        internal IReadOnlyList<StackRowPaint> StackRows => game.stackRows;
        internal IReadOnlyList<StackWorkloadPaint> StackWorkloads => game.stackWorkloadPaint;
        internal bool ResolvingCapturedStack => game.ResolvingCapturedStack;
        internal int StackResolutionRemaining => game.CapturedStackRemaining;
        internal Mana.Client.FiniteResolutionIntent? FiniteResolution => game.finiteResolutionIntent;
        internal Mana.Client.FiniteResolutionProgress? FiniteResolutionProgress => game.finiteProgressProjection?.Progress ?? game.lastFiniteProgress;
        internal int AnnouncementCount => game.matchPresentation.AnnouncementCount;
        internal string? AnnouncementId => game.matchPresentation.Announcement(game.now)?.Id;
        internal ManaJourneyPaint? ManaJourney => game.manaJourneyPaint;
        internal IReadOnlyList<ManaSourcePaint> ManaSources => game.manaSourcePaint;
        internal IReadOnlyList<ManaColorPaint> ManaColors => game.manaColorPaint;
        internal string? ReferencePreviewCardId => game.pendingPreviewCardId;
        internal string? ReferencePreviewStackId => game.pendingPreviewReference?.StackId;
        internal Rectangle? ReferencePreviewBounds => game.pendingPreviewBounds;
        internal IReadOnlyDictionary<int, Rectangle> PrioritySeats => game.prioritySeats;
        internal Mana.Magic.TurnProgressView? TurnProgress => game.turnProgress;
        internal void OpenBattlefield(int owner) => game.OpenZone(owner, "Battlefield");
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
        internal bool PreparingTableArtwork => game.tableArtwork.Waiting;
        internal Mana.Table.Presentation.ArtworkReadiness TableArtworkProgress => game.tableArtwork.Progress;
        internal string ActivityDetail => game.activityDetail;
        internal string ActivityStage => game.activityStage.ToString() ?? "";
        internal bool StartupFailed => game.startupFailed;
        internal ImmutableArray<DeckPreset> Presets => game.presets;
        internal DeckSummary? SelectedDeck => game.selectedDeck;
        internal ImmutableArray<DeckSummary> SavedDecks => game.decks;
        internal ImmutableArray<DeckLibraryDeletion> DeletedDecks => game.deletedDecks;
        internal void RefreshDeckLibrary() => game.RefreshDeletedDecks();
        internal DeckDetails? DeckDetails => game.selectedDeckDetails;
        internal bool DeckEditor => game.deckEditor;
        internal DeckTokenCatalog? DeckTokens => game.CurrentDeckTokens;
        internal CatalogPage? TokenPrintings => game.tokenPrintingsRead.View.Value;
        internal bool AnalysisOpen => game.analysisOpen;
        internal DeckAnalysisState? Analysis => game.deckWorkspace.View.Analysis;
        internal bool AnalysisCurrent => game.AnalysisCurrent;
        internal string[] AnalysisVisibleRecommendationIds => game.deckWorkspace.View.Analysis is { } a ? game.VisibleRecommendations(a).Select(r => r.Id).ToArray() : [];
        internal bool UpdatesOpen => game.updatesOpen;
        internal bool UpdateChecking => game.UpdateView.Checking;
        internal bool UpdateDownloading => game.UpdateView.Downloading;
        internal bool UpdateValidating => game.UpdateView.Verifying;
        internal bool UpdateInstallerLaunched => game.UpdateView.InstallerLaunched;
        internal bool UpdateCheckOnStartup => game.updateSession.CheckOnStartup;
        internal bool AutomaticUpdateCheckAllowed => game.AutomaticUpdateCheckAllowed;
        internal string UpdateError => game.UpdateView.Error;
        internal string UpdateNotice => game.UpdateView.Notice;
        internal string UpdateInstallBlocker => game.UpdateInstallBlocker();
        internal long UpdateBytes => game.UpdateView.BytesReceived;
        internal Mana.Client.AppRelease? AvailableUpdate => game.UpdateView.Release;
        internal Mana.Client.PreparedUpdate? PreparedUpdate => game.UpdateView.Prepared;
        internal void OpenAppUpdates() => game.OpenAppUpdates();
        internal void CheckAppUpdates() => game.CheckAppUpdates();
        internal void Post(Action action) => game.updates.Enqueue(action);
        internal void SetUpdateGuardState(bool connected = false, string saveError = "", bool text = false) {
            game.session.BindLobby(connected ? new("hosting", "Controlled table", null, false, null, false, "", null, [], []) : null);
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
        internal CatalogPage? Catalog => game.catalogRead.View.Value;
        internal CatalogPage? Printings => game.printingRead.View.Value;
        internal bool CatalogBusy => game.catalogRead.Busy;
        internal void TypeDeckText(string text) { foreach (char c in text) game.DeckText(c); }
        internal void DeckQuantity(int quantity) => game.DeckQuantity(quantity);
        internal void RenameDeck(string name) => game.RenameDeck(name);
        internal void DuplicateDeck(string name) => game.DuplicateDeck(name);
        internal void EditPreset(string id) => game.ImportDeck(() => game.catalog.ImportPresetAsync(id), true);
        internal WorkshopState? Workshop => game.deckWorkspace.View.Workshop;
        internal DeckVersionView? WorkshopVersion => game.workshopVersionView;
        internal void ShowSourcePreview(DeckSourcePreview preview) {
            game.workshopSourcePreview = preview; game.workshopSourceImport = false; game.workshopSourceChoice = "";
            game.workshopDialog = "source-preview"; game.WorkshopDialogPage = 0;
        }
        internal bool WorkshopCurrent => game.WorkshopCurrent;
        internal HandPractice? WorkshopHand => game.deckWorkspace.View.Hand;
        internal ImmutableArray<DeckTest> WorkshopTests => game.deckWorkspace.View.Tests;
        internal string WorkshopTab => game.workshopTab;
        internal string WorkshopDialog => game.workshopDialog;
        internal string WorkshopError => game.deckWorkspace.View.WorkshopError;
        internal void SetWorkshopPlan(string plan, string question) => game.SaveWorkshopMetadata(new(Plan: plan, Question: question), "Test plan saved.");
        internal void SaveWorkshopNote(string testId, string note) => game.SaveWorkshopTestNote(game.deckWorkspace.View.Tests.First(t => t.Id == testId), note);
        internal void ConcedeWorkshop() => game.ReturnToWorkshop();
        internal Lobby? Lobby => game.lobby;
        internal bool LobbyAiSeatControlsAvailable => game.CanManageLobbySeats;
        internal IDisposable PauseObservationScheduling()
        {
            double original = game.nextPoll;
            game.nextPoll = double.MaxValue;
            // Stop only future scheduling. An existing observation keeps its
            // actual owner, epoch and completion; the probe must await it.
            return new ObservationSchedulingPause(game, original);
        }
        private sealed class ObservationSchedulingPause(TableApplication owner, double original) : IDisposable
        {
            private TableApplication? current = owner;
            public void Dispose()
            {
                var restore = Interlocked.Exchange(ref current, null);
                if (restore != null) restore.nextPoll = original;
            }
        }
        internal void PresentLobby(Lobby state, bool pausePolling) { game.session.BindLobby(state); game.nextPoll = pausePolling ? double.MaxValue : game.now; }
        internal Mana.Table.Presentation.PresentationFrame ComposeLobbySnapshot(Lobby state)
        {
            if (game.busy || game.polling || game.match != null || game.lobby is not { } original || state.TableId != original.TableId)
                throw new InvalidOperationException("Render a lobby snapshot only between commands and observations of the same table.");
            var originalFrame = game.ReadPresentation();
            var time = new GameTime(TimeSpan.FromSeconds(game.now), TimeSpan.Zero);
            try {
                // Compose only: a foreign viewer never reaches Update, input,
                // the deck coordinator, or any transport command.
                game.session.BindLobby(state);
                game.PreparePresentation(time);
                return game.ReadPresentation();
            } finally {
                game.session.BindLobby(original);
                game.PreparePresentation(time); // Restore current host hit regions too.
                game.Frame = originalFrame;
            }
        }
        internal void ImportDeck(string name, string text) => game.ImportDeck(() => game.catalog.ImportAsync(name, text), true);
        internal SoloSetup? SoloSetup => game.soloSetup;
        internal int[] SoloChoices => game.soloChoices;
        internal bool LocalMatch => game.localMatch;
        internal bool NeedsRecovery => game.session.NeedsRecovery;
        internal float TextScale => game.preferences.TextScale;
        internal Rectangle DecisionTextBounds => game.decisionTextBounds;
        internal Rectangle DecisionPanelBounds => game.decisionPanelBounds;
        internal string DecisionText => game.decisionText;
        internal bool DecisionTextOverflow => game.decisionTextOverflow;
        internal Mana.Magic.DecisionDockView? DecisionDock => game.decisionDock;
        internal string HelpPageText => game.helpPageText;
        internal IReadOnlyList<BattlefieldLabelPaint> BattlefieldLabels(string id) => game.battlefieldLabels.TryGetValue(id, out var labels) ? labels : [];
        internal Mana.Renderer.TableScene? TableScene => game.tableScene;
        internal IReadOnlyList<CombatRelationPaint> CombatRelations => game.combatRelations;
        internal bool GroupedCombatReview => game.GroupedCombatReview;
        internal Mana.Renderer.BoardView CardSpreadView => game.spreadView;
        internal void ImportFilePath(string path) => game.ImportFilePath(path);
        internal void SetConnectedTable(Lobby state) { game.localMatch = false; game.session.BindLobby(state); }
        internal bool Automatic => game.automatic;
        internal bool Held => game.held;
        internal PublicCounterBadge? CounterBadge(int player) => game.publicCounterBadges.TryGetValue(player, out var badge) ? badge : null;
        internal PublicResourcePaint? ManaPoolBadge(int player) => game.manaPoolBadges.TryGetValue(player, out var badge) ? badge : null;
        internal PublicResourcePaint? ManaPoolDetail => game.overlay == "mana" ? game.manaPoolDetail : null;
        internal AttachmentBadgePaint? AttachmentBadge(string id) => game.attachmentBadges.TryGetValue(id, out var badge) ? badge : null;
        internal IReadOnlyList<AttachmentConnection> AttachmentConnections => game.attachmentConnections;
        internal bool HomeVisible => game.match == null && !game.deckEditor && game.setupModal.Length == 0
            && !game.tableGuideOpen && !game.StartupVisible;
        internal bool TableGuideOpen => game.tableGuideOpen;
        internal bool ExperienceOpen => game.ExperienceOpen;
        internal void OpenTableGuide() => game.OpenTableGuide();
        internal void SetHeld(bool value) => game.held = value;
        internal Mana.Renderer.ResolutionCue? LastResolution => game.matchPresentation.LastResolution;
        internal IReadOnlyList<ImpactPaint> Impacts => game.impactPaint;
        internal IReadOnlyList<(string CardId, string Destination, Mana.Renderer.Pose Pose)> PublicDepartures => game.matchPresentation.Departures
            .Where(d => game.now - d.Started <= 1.5).Select(d => (d.Card.VisualId, d.Destination, game.DeparturePose(d))).ToArray();
        internal void SetAutomatic(bool value) => game.SetAutomatic(value);
        internal IReadOnlyList<int> Selections => game.decisions.Selections;
        internal IReadOnlyDictionary<int, ChoiceOrderBadge> ChoiceOrderBadges => game.choiceOrderBadges;
        internal IReadOnlyList<int> AllocationAmounts => game.decisions.Allocations;
        internal Mana.Table.Interaction.AllocationInspection? AllocationDetail => game.decisions.Allocation?.Inspection;
        internal string? AllocationProblem => game.decisions.Payload is AllocationPayload allocation ? Mana.Magic.AllocationGuide.Problem(allocation, game.decisions.Allocations) : null;
        internal string Overlay => game.overlay;
        internal string? ZoneName => game.zoneName;
        internal IReadOnlyList<Hit> Hits => game.hits.Published;
        internal string Scope => game.Scope;
        internal int DesignHeight => game.DesignHeight;
        internal Hit? HitAt(Point p) => game.HitAt(p);
        internal bool CurrentHit(Hit hit) => game.CurrentHit(hit);
        internal IEnumerable<Card> AllCards() => game.AllCards();
        internal bool SoloTab => game.soloTab;
        internal TableServices Services => game.services;
        internal string? SelectedCombat => game.selectedCombat;
        internal void Accept(GameSnapshot? state) => game.Accept(state);
        internal string? InspectedStack => game.inspectedStack;
        internal int ZonePage => game.zonePage;
        internal Hit? Pressed { get => game.pressed; set => game.pressed = value; }
        internal MouseState PreviousMouse { get => game.previousMouse; set => game.previousMouse = value; }
        internal double Now => game.now;
        internal Rectangle? HoverPreview => game.hoverPreview;
        internal Card? Hovered => game.hovered;
        internal int CardPaintCount(string id) => game.matchPresentation.Cards.CompositionCount(id);
        internal Texture2D? Art(Card card) => game.resources.Card(card);
        internal Mana.Renderer.Pose? DisplayedPose(string id) => game.matchPresentation.Cards.TryGetPose(id, out var pose) ? pose : null;
    }
}
