using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Scenarios;

/// <summary>Explicit presentation fixture connection. It cannot start Forge, connect to tables or write decks.</summary>
public sealed class ExperienceLabEngine(ExperienceLabSession session) : ICardEngine, ISourceActions
{
    public ExperienceLabSession Session { get; } = session;
    public string Name => "Scripted experience lab";
    public Task InitializeAsync() => Task.CompletedTask;
    public Task<GameSnapshot?> ObserveAsync() => Task.FromResult<GameSnapshot?>(Session.State);
    public Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey) =>
        Task.FromResult(Session.SourceActions(gameId, decisionId, sourceCardKey));
    public Task ExecuteAsync(GameCommand reply) { Session.Reply(reply); return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private static Exception Unavailable() => new EngineCommandRejectedException("The experience lab uses isolated scripted snapshots. Real decks, networking and game rules are available in the normal app.");
    private static Task<T> No<T>() => Task.FromException<T>(Unavailable());
    private static Task No() => Task.FromException(Unavailable());
    public Task<ImmutableArray<DeckSummary>> DecksAsync() => Task.FromResult<ImmutableArray<DeckSummary>>([]);
    public Task<ImmutableArray<DeckPreset>> PresetsAsync() => Task.FromResult<ImmutableArray<DeckPreset>>([]);
    public Task<DeckSummary> ImportAsync(string name, string text) => No<DeckSummary>();
    public Task<DeckSummary> ImportPresetAsync(string id) => No<DeckSummary>();
    public Task<DeckDetails> OpenDeckAsync(string id) => No<DeckDetails>();
    public Task<DeckDetails> SetCommandersAsync(string deckId, long revision, ImmutableArray<string> cardIds) => No<DeckDetails>();
    public Task<CatalogPage> SearchCardsAsync(CardQuery query) => No<CatalogPage>();
    public Task<DeckDetails> CreateDeckAsync(string name) => No<DeckDetails>();
    public Task<DeckDetails> EditDeckAsync(string deckId, long revision, ImmutableArray<DeckEdit> edits) => No<DeckDetails>();
    public Task<DeckDetails> RenameDeckAsync(string deckId, long revision, string name) => No<DeckDetails>();
    public Task<DeckDetails> DuplicateDeckAsync(string deckId, long revision, string name) => No<DeckDetails>();
    public Task<DeckDetails> UndoDeckAsync(string deckId, long revision) => No<DeckDetails>();
    public Task<DeckDetails> RedoDeckAsync(string deckId, long revision) => No<DeckDetails>();
    public Task<DeckDetails> SaveDeckAsync(string deckId, long revision) => No<DeckDetails>();
    public Task<string> ExportDeckAsync(string deckId, long revision) => No<string>();
    public Task<SoloSetup> PrepareSoloAsync(string deckId) => No<SoloSetup>();
    public Task StartSoloAsync(string deckId, ImmutableArray<string> opponentIds) => No();
    public Task<Lobby> HostAsync(int players, bool automaticPortForwarding) => No<Lobby>();
    public Task<Lobby> JoinAsync(string address) => No<Lobby>();
    public Task<Lobby> LobbyAsync() => No<Lobby>();
    public Task<Lobby> SelectDeckAsync(LobbyDeckSelection selection) => No<Lobby>();
    public Task SetReadyAsync(bool ready) => No();
    public Task StartAsync() => No();
    public Task ReturnToLobbyAsync() => No();
    public Task LeaveAsync() => No();
    public Task ConcedeAsync(string gameId) => No();
}
