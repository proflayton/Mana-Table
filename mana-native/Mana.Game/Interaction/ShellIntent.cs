using Mana.Contracts;

namespace Mana.Table.Interaction;

internal enum ShellPage { HomeDecks, DeckPicker, Review, Connection, LobbyAi, LabGallery }
internal enum DeckCollection { Saved, Presets, Deleted }

// Shell interactions describe the player's requested transition. The published
// frame carries no closure over application navigation or asynchronous work.
internal abstract record ShellIntent : UiIntent
{
    internal sealed record DismissError : ShellIntent;
    internal sealed record ReturnToSetup : ShellIntent;
    internal abstract record Lobby : ShellIntent { internal override InteractionScopeKind ScopeKind => InteractionScopeKind.Home; }
    internal sealed record ChoosePlayMode(bool Solo) : Lobby;
    internal sealed record OpenPlayHome : Lobby;
    internal sealed record OpenDeckLibrary : Lobby;
    internal sealed record EditDeck(bool ClearInput = false, bool AtReviewCard = false) : Lobby;
    internal sealed record ReviewDeck(bool ClearInput = false) : Lobby;
    internal sealed record SelectHomeDeck(string DeckId) : Lobby;
    internal sealed record Page(ShellPage Target, int Delta) : Lobby
    {
        internal override InteractionScopeKind ScopeKind => Target == ShellPage.LabGallery ? InteractionScopeKind.Current : InteractionScopeKind.Home;
    }
    internal sealed record OpenOpponentPicker(int Seat) : Lobby;
    internal sealed record SetFriendCount(int Players) : Lobby;
    internal sealed record Connect(bool Hosting) : Lobby;
    internal sealed record OpenJoin : Lobby;
    internal sealed record OpenConnection(bool ClearInput = false) : Lobby;
    internal sealed record LeaveLobby : Lobby;
    internal sealed record SetReady(bool Ready) : Lobby;
    internal sealed record StartSolo : Lobby;
    internal sealed record StartFriends : Lobby;
    internal sealed record CloseSetup : Lobby;
    internal sealed record ShowReview(bool List) : Lobby;
    internal sealed record FocusSetupInput(bool Clear = false, bool ResetSelection = true) : Lobby;
    internal sealed record TogglePortForwarding : Lobby;
    internal sealed record CopyInternetInvite : Lobby;
    internal sealed record CopyAddress(string Url) : Lobby;
    internal sealed record SelectDeckCollection(DeckCollection Collection) : Lobby;
    internal sealed record ChoosePickerDeck(string DeckId) : Lobby;
    internal sealed record FlipReviewCard : Lobby;
    internal sealed record SelectReviewSection(string Section) : Lobby;
    internal sealed record SelectReviewCard(string CardId, string Section) : Lobby;
    internal sealed record ResizeLobby(int Players) : Lobby;
    internal sealed record SetTempo(TempoPreset Preset, TempoExpiry Expiry) : Lobby;
    internal sealed record OpenLobbyAi(int Seat) : Lobby;
    internal sealed record SelectLobbyAi(string OpponentId) : Lobby;
    internal sealed record RemoveLobbyAi(int Seat) : Lobby;
    internal sealed record ImportFile : Lobby;
    internal sealed record ImportClipboard : Lobby;
    internal sealed record EditPlayerName : Lobby;
    internal sealed record SavePlayerName : Lobby;

    internal abstract record Update : ShellIntent { internal override InteractionScopeKind ScopeKind => InteractionScopeKind.Updates; }
    internal sealed record OpenUpdates : Update;
    internal sealed record CloseUpdates : Update;
    internal sealed record CheckUpdates : Update;
    internal sealed record DownloadUpdate : Update;
    internal sealed record CancelUpdateDownload : Update;
    internal sealed record InstallUpdate : Update;
    internal sealed record OpenUpdateLocation(bool Folder = false) : Update;
    internal sealed record ToggleStartupUpdateCheck : Update;

    internal abstract record Social : ShellIntent { internal override InteractionScopeKind ScopeKind => InteractionScopeKind.Chat; }
    internal sealed record ToggleChat : Social;
    internal sealed record BlurChat : Social;
    internal sealed record ShowMessages : Social;
    internal sealed record ShowPeople : Social;
    internal sealed record ReadMessage(long MessageId) : Social;
    internal sealed record CloseMessage : Social;
    internal sealed record OlderMessages : Social;
    internal sealed record NewerMessages : Social;
    internal sealed record LatestMessages : Social;
    internal sealed record SendMessage : Social;
    internal sealed record SendEmote(string Text) : Social;
    internal sealed record FocusChat : Social;
    internal sealed record ToggleTableMute : Social;
    internal sealed record ToggleMemberMute(string MemberId) : Social;

    internal abstract record Activity : ShellIntent { internal override InteractionScopeKind ScopeKind => InteractionScopeKind.Activity; }
    internal sealed record RetryStartup : Activity;
    internal sealed record ExitStartup : Activity;
    internal sealed record DismissActivity : Activity;
    internal abstract record Guide : ShellIntent { internal override InteractionScopeKind ScopeKind => InteractionScopeKind.Experience; }
    internal sealed record OpenGuide : Guide { internal override InteractionScopeKind ScopeKind => InteractionScopeKind.Current; }
    internal sealed record CloseGuide : Guide;
    internal abstract record Lab : ShellIntent;
    internal sealed record ToggleTableStudy : Lab;
    internal sealed record ToggleTurnFollow : Lab;
    internal sealed record ShowWholeTable : Lab;
    internal sealed record OpenLabGallery : Lab;
    internal sealed record CloseLabGallery : Lab;
    internal sealed record ReplayLabStage(int Stage) : Lab;
    internal sealed record SelectLabScenario(string ScenarioId) : Lab;
    internal sealed record ExplainLabChange : Lab;
    internal sealed record ExitLab : Lab;
}
