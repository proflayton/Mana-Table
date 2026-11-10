using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.Contracts;

namespace Mana.Forge;

public sealed record ForgePaths(string Java, string Jar, string Resources, string Profile);

public sealed partial class ForgeEngine(ForgePaths paths, TimeProvider? requestClock = null) : ICardEngine, ITableSocial, IDeckWorkshop, ITableSeatSetup, ITableMatchSetup, ITableEncounterStart, ICardPrintings, ISourceActions, ITableTempo, IAutomatedSeatProvider, ITablePresence
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private sealed record PendingRequest(string Method, TaskCompletionSource<JsonElement> Completion, CancellationToken Cancellation);
    private readonly ConcurrentDictionary<long, PendingRequest> pending = new();
    private readonly SemaphoreSlim write = new(1);
    private readonly object worldScopeGate = new();
    private long worldGeneration;
    private readonly TimeProvider requestTime = requestClock ?? TimeProvider.System;
    // Readers retain their own process and ready signal across a failed startup.
    private sealed class EngineProcess(Process process)
    {
        public Process Process { get; } = process;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Reader { get; set; }
        public Task? Diagnostics { get; set; }
        public Task? Stopped { get; set; }
        public bool SourceActionsAvailable { get; set; }
        public bool DeckLibraryAvailable { get; set; }
        public bool DeckTokensAvailable { get; set; }
        public bool TempoAvailable { get; set; }
        public bool AutomationAvailable { get; set; }
        public bool ConfiguredSimulationAvailable { get; set; }
        public bool CertifiedUndoAvailable { get; set; }
        public bool HistoryAvailable { get; set; }
        public bool HistoryAnnouncementsAvailable { get; set; }
        public bool AutomatedHistoryAvailable { get; set; }
        public bool MatchSetupAvailable { get; set; }
        public bool EncounterStartAvailable { get; set; }
        public bool OwnedDecisionAvailable { get; set; }
        public bool PrivateActionFacesAvailable { get; set; }
        public bool TablePresenceAvailable { get; set; }
        public bool TableChoicesAvailable { get; set; }
        public bool PriorityContinuationAvailable { get; set; }
        public bool AutomatedPriorityContinuationAvailable { get; set; }
        public bool AutomatedPriorityMetadataAvailable { get; set; }
        public ForgeCommandScope Commands { get; } = new();
        public volatile Exception? Failure;
        public volatile bool FramingFailed;
    }
    private EngineProcess? current;
    private long sequence;
    private bool disposed;
    public string Name => "Forge";
    public bool SupportsPriorityContinuation => current is { PriorityContinuationAvailable: true, Failure: null } owner
        && owner.Ready.Task.IsCompletedSuccessfully && !owner.Process.HasExited;
    public async Task InitializeAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (current is { } previous) {
            if (previous.Ready.Task.IsCompletedSuccessfully && previous.Failure == null && !previous.Process.HasExited) return;
            Fail(previous, new IOException("The engine is not running"));
            try { await StopAsync(previous); }
            finally { if (current == previous) current = null; }
        }
        Directory.CreateDirectory(paths.Profile);
        var start = new ProcessStartInfo(paths.Java) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = System.Text.Encoding.UTF8 };
        foreach (var arg in new[] { "-Xmx2g", "-Dfile.encoding=UTF-8", "-Djava.awt.headless=true", "-jar", paths.Jar, paths.Resources, Path.Combine(paths.Profile, "decks") }) start.ArgumentList.Add(arg);
        var owner = new EngineProcess(Process.Start(start) ?? throw new IOException("Could not start Forge"));
        current = owner;
        owner.Diagnostics = Task.Run(async () => {
            await using var log = new StreamWriter(Path.Combine(paths.Profile, "engine.log"), true);
            while (await owner.Process.StandardError.ReadLineAsync() is { } line) { await log.WriteLineAsync(line); await log.FlushAsync(); }
        });
        owner.Reader = Task.Run(async () => {
            try {
                while (await owner.Process.StandardOutput.ReadLineAsync() is { } line) {
                    using var document = JsonDocument.Parse(line);
                    var message = document.RootElement;
                    if (message.TryGetProperty("event", out var ev)) {
                        if (ev.GetString() == "ready") {
                            var capabilities = ForgeProtocol.ReadyCapabilities(message);
                            owner.SourceActionsAvailable = capabilities.Contains("source-actions");
                            owner.DeckLibraryAvailable = capabilities.Contains("deck-library");
                            owner.DeckTokensAvailable = capabilities.Contains("deck-tokens");
                            owner.TempoAvailable = capabilities.Contains("decision-tempo");
                            owner.AutomationAvailable = capabilities.Contains("automated-seats");
                            owner.ConfiguredSimulationAvailable = capabilities.Contains("simulation-match-setup");
                            owner.CertifiedUndoAvailable = capabilities.Contains("certified-one-step-undo");
                            owner.HistoryAvailable = capabilities.Contains("match-history");
                            owner.HistoryAnnouncementsAvailable = capabilities.Contains("history-announcements");
                            owner.AutomatedHistoryAvailable = capabilities.Contains("automated-match-history");
                            owner.MatchSetupAvailable = capabilities.Contains("match-setup");
                            owner.EncounterStartAvailable = capabilities.Contains("durable-encounter-admission");
                            owner.OwnedDecisionAvailable = capabilities.Contains("owned-decision-observation");
                            owner.PrivateActionFacesAvailable = capabilities.Contains("private-action-faces");
                            owner.TablePresenceAvailable = capabilities.Contains("table-presence");
                            owner.TableChoicesAvailable = capabilities.Contains("table-choices-v1");
                            owner.PriorityContinuationAvailable = capabilities.Contains("priority-continuation-v1");
                            owner.AutomatedPriorityContinuationAvailable = capabilities.Contains("automated-priority-continuation-v1");
                            owner.AutomatedPriorityMetadataAvailable = capabilities.Contains("automated-priority-metadata-v1");
                            owner.Ready.TrySetResult();
                        }
                        else if (ev.GetString() == "error") throw new IOException(Text(message, "message"));
                    } else if (message.TryGetProperty("id", out var id) && pending.TryRemove(id.GetInt64(), out var reply)) {
                        if (reply.Cancellation.IsCancellationRequested) {
                            reply.Completion.TrySetCanceled(reply.Cancellation);
                            continue;
                        }
                        if (message.TryGetProperty("error", out _)) reply.Completion.TrySetException(RequestFailure(reply.Method, message));
                        else {
                            CommitAutomatedWorldReplacement(owner, reply.Method);
                            reply.Completion.TrySetResult(message.GetProperty("result").Clone());
                        }
                    }
                }
                if (!disposed) throw new IOException("The rules engine closed. See engine.log in your profile.");
            } catch (Exception ex) { Fail(owner, ex); }
        });
        try { await owner.Ready.Task.WaitAsync(TimeSpan.FromSeconds(120)); }
        catch (Exception ex) {
            Fail(owner, ex);
            try { await StopAsync(owner); }
            catch (Exception) { /* Preserve the startup failure reported to the player. */ }
            finally { if (current == owner) current = null; }
            throw;
        }
    }
    private void Fail(EngineProcess owner, Exception ex)
    {
        owner.Commands.Clear();
        owner.Failure = ex;
        owner.Ready.TrySetException(ex);
        if (current != owner) return;
        foreach (var item in pending) if (pending.TryRemove(item.Key, out var reply)) reply.Completion.TrySetException(ex);
    }
    private async Task<JsonElement> Request(string method, object? arguments = null, CancellationToken cancellation = default,
        EngineProcess? expectedOwner = null, Action? beforeDispatch = null)
    {
        cancellation.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        var owner = expectedOwner ?? current ?? throw new IOException("The engine is not running");
        if (expectedOwner != null && current != expectedOwner) throw new EngineObservationSupersededException();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45), requestTime);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        var token = operation.Token;
        EngineRequestPhase phase = EngineRequestPhase.Ready;
        if (method is "matchStart" or "workshopStartTest" or "multiplayerHost" or "multiplayerJoin"
            or "multiplayerStart" or "startEncounter" or "retryEncounterStart" or "cancelEncounterStart"
            or "multiplayerReturn" or "multiplayerClose" or "matchConcede") owner.Commands.Clear();
        long id = 0;
        try {
            await owner.Ready.Task.WaitAsync(token).ConfigureAwait(false);
            if (current != owner || owner.Failure != null || owner.Process.HasExited) throw new IOException("The engine is not running");
            id = Interlocked.Increment(ref sequence);
            var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = new(method, reply, token);
            using var abandoned = token.Register(() => {
                if (pending.TryRemove(id, out var cancelled)) cancelled.Completion.TrySetCanceled(token);
            });
            string line = JsonSerializer.Serialize(new { id, method, @params = arguments ?? new { } });
            phase = EngineRequestPhase.WriteGate;
            await write.WaitAsync(token).ConfigureAwait(false);
            Exception? damagedFrame = null;
            try {
                token.ThrowIfCancellationRequested();
                if (current != owner || owner.Failure != null || owner.Process.HasExited) throw new IOException("The engine is not running");
                beforeDispatch?.Invoke();
                phase = EngineRequestPhase.Writing;
                var writing = owner.Process.StandardInput.WriteLineAsync(line.AsMemory(), token);
                ObserveAbandonedRequestFault(writing);
                await writing.WaitAsync(token).ConfigureAwait(false);
                phase = EngineRequestPhase.Flushing;
                var flushing = owner.Process.StandardInput.FlushAsync(token);
                ObserveAbandonedRequestFault(flushing);
                await flushing.WaitAsync(token).ConfigureAwait(false);
            } catch (Exception error) when (phase is EngineRequestPhase.Writing or EngineRequestPhase.Flushing) {
                damagedFrame = error is OperationCanceledException
                    ? new IOException("The rules engine connection was interrupted while writing.", error) : error;
                // Fence a queued writer before releasing the gate. Complete
                // failures only afterward, so no awakened callback owns it.
                owner.Failure = damagedFrame;
                owner.FramingFailed = true;
                throw;
            } finally {
                write.Release();
                if (damagedFrame != null) {
                    pending.TryRemove(id, out _);
                    Fail(owner, damagedFrame);
                }
            }
            phase = EngineRequestPhase.Reply;
            var result = await reply.Task.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        } catch (OperationCanceledException error) when (token.IsCancellationRequested) {
            if (cancellation.IsCancellationRequested)
                throw new OperationCanceledException("The request was cancelled.", error, cancellation);
            throw new EngineRequestTimeoutException(method switch {
                "automatedEnable" => "automation-enable", "automatedState" => "automation-observe",
                "automatedAction" => "automation-execute", "automatedFault" => "automation-fault", _ => "engine-request"
            }, phase, error);
        } finally { if (id != 0) pending.TryRemove(id, out _); }
    }
    private static void ObserveAbandonedRequestFault(Task operation) => _ = operation.ContinueWith(task => { _ = task.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    internal static Exception RequestFailure(string method, JsonElement message)
    {
        if (method is "automatedAction" or "automatedPriorityControl" && Text(message, "errorCode") == "automated-priority-control-retired")
            return new ForgeAutomatedPriorityRetiredException();
        if (method == "tablePresence" && Text(message, "errorCode") == "table-presence-superseded")
            return new EngineObservationSupersededException();
        if (method == "matchSourceActions") {
            if (Text(message, "errorCode") == "source-read-superseded")
                return new EngineSourceReadException(EngineSourceReadFailure.Superseded);
            if (Text(message, "errorCode") == "source-read-changed")
                return new EngineSourceReadException(EngineSourceReadFailure.Changed);
            if (Text(message, "errorCode") == "source-read-failed")
                return new EngineSourceReadException(EngineSourceReadFailure.Failed);
        }
        // A racing observation has no command receipt to retry. Only the
        // provider's typed discovery result on this read-only method permits
        // discarding a batch; identical prose on any other failure stays fatal.
        if (method == "automatedState" && Text(message, "errorCode") == "observation-superseded")
            return new EngineObservationSupersededException();
        if (method == "matchDecisionObservation" && Text(message, "errorCode") == "observation-superseded")
            return new EngineObservationSupersededException();
        if (method == "matchDecisionObservation" && Text(message, "errorCode") == "unsupported-capability")
            return new NotSupportedException("This table does not expose owned decision observations.");
        if (method is "matchHistory" or "automatedMatchHistory" && Text(message, "errorCode") == "history-unavailable")
            return new NotSupportedException("This table does not expose match history.");
        return new EngineCommandRejectedException(Text(message, "error") is { Length: > 0 } error
            ? error : "The rules engine rejected this request.");
    }
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";
    private static T Read<T>(JsonElement value) => value.Deserialize<T>(Json) ?? throw new IOException("Missing engine response");
    private static DeckSummary Deck(JsonElement data) {
        return new(Text(data, "id"), data.TryGetProperty("deck", out var deck) ? Text(deck, "name") : Text(data, "name")) {
            LibraryToken = Text(data, "libraryToken")
        };
    }
    public async Task<ImmutableArray<DeckSummary>> DecksAsync() => Read<ImmutableArray<DeckSummary>>((await Request("list")).GetProperty("decks"));
    public async Task<ImmutableArray<DeckPreset>> PresetsAsync() => Read<ImmutableArray<DeckPreset>>(await Request("deckPresets"));
    public async Task<DeckSummary> ImportAsync(string name, string text) => Deck(await Request("import", new { name, text, format = "Commander" }));
    public async Task<DeckSummary> ImportPresetAsync(string id) => Deck(await Request("presetImport", new { id }));
    private static Card MapCatalogFace(JsonElement card) => new() {
        GamePieceType = Text(card, "deckSection") == "Tokens" ? "token" : "card",
        Name = Text(card, "name"), ArtName = Text(card, "artName"), ArtFace = Text(card, "artFace"),
        Edition = Text(card, "edition"), ArtSet = Text(card, "artSet"), CollectorNumber = Text(card, "collectorNumber"),
        ArtIndex = card.TryGetProperty("artIndex", out var artIndex) ? artIndex.GetInt32() : 0,
        Foil = card.TryGetProperty("foil", out var foil) && foil.GetBoolean(),
        ManaCost = Text(card, "manaCost"), Type = Text(card, "type"), Text = Text(card, "oracleText"),
        Power = int.TryParse(Text(card, "power"), out var power) ? power : null,
        Toughness = int.TryParse(Text(card, "toughness"), out var toughness) ? toughness : null,
        OtherFace = card.TryGetProperty("otherFace", out var face) && face.ValueKind == JsonValueKind.Object ? MapOtherFace(face) : null
    };
    private static CardFace MapOtherFace(JsonElement face) => new(Text(face, "name"), Text(face, "type"),
        Text(face, "manaCost"), Text(face, "oracleText"),
        int.TryParse(Text(face, "power"), out var power) ? power : null,
        int.TryParse(Text(face, "toughness"), out var toughness) ? toughness : null,
        Text(face, "artName"), Text(face, "artFace"));
    private static CatalogCard MapCatalogCard(JsonElement card) => new(Text(card, "id"), MapCatalogFace(card),
        card.GetProperty("manaValue").GetInt32(), card.GetProperty("colorIdentity").GetInt32(), Text(card, "deckSection"), Text(card, "edition"));
    private static DeckDetails MapDeck(JsonElement data)
    {
        var deck = data.GetProperty("deck");
        var entries = deck.GetProperty("entries").EnumerateArray().Select(entry => {
            var card = MapCatalogCard(entry.GetProperty("card"));
            return new DeckEntry(Text(entry, "section"), card.Id, card.Card, entry.GetProperty("quantity").GetInt32(), card.ManaValue, card.ColorIdentity, card.Edition);
        }).ToImmutableArray();
        var validation = data.GetProperty("validation");
        return new(Text(data, "id"), Text(deck, "name"), deck.GetProperty("revision").GetInt64(), entries,
            Read<ImmutableArray<CommanderOption>>(data.GetProperty("commanderChoices")), Text(validation, "problem"), Text(data, "saveError"),
            deck.GetProperty("canUndo").GetBoolean(), deck.GetProperty("canRedo").GetBoolean()) {
                Issues = validation.TryGetProperty("issues", out var issues) ? Read<ImmutableArray<DeckIssue>>(issues) : [],
                StructureValid = validation.TryGetProperty("structureValid", out var structural) ? structural.GetBoolean() : null,
                Eligibility = Text(validation, "eligibility") is { Length: > 0 } eligibility ? eligibility : "unknown",
                Format = Text(data, "format"),
                LibraryToken = Text(data, "libraryToken"),
                TokenPreviews = data.TryGetProperty("tokenPreviews", out var previews) ? previews.EnumerateArray().Select(MapCatalogFace).ToImmutableArray() : []
            };
    }
    public async Task<DeckDetails> OpenDeckAsync(string id) => MapDeck(await Request("open", new { id }));
    public async Task<DeckDetails> SetCommandersAsync(string deckId, long revision, ImmutableArray<string> cardIds) =>
        MapDeck(await Request("setCommanders", new { deckId, revision, cardIds }));
    public async Task<CatalogPage> SearchCardsAsync(CardQuery query)
    {
        var data = await Request("search", new { text = query.Text, type = query.Type, colorIdentity = query.ColorIdentity,
            maxManaValue = query.MaxManaValue, sort = query.Sort, offset = query.Offset, limit = query.Limit, unique = true });
        return new(data.GetProperty("total").GetInt32(), data.GetProperty("offset").GetInt32(),
            data.GetProperty("cards").EnumerateArray().Select(MapCatalogCard).ToImmutableArray());
    }
    public async Task<DeckDetails> CreateDeckAsync(string name) => MapDeck(await Request("new", new { name, format = "Commander" }));
    public async Task<CatalogPage> CardPrintingsAsync(string cardId, int offset = 0, int limit = 6, string text = "", string finish = "")
    {
        var data = await Request("cardPrintings", new { cardId, offset, limit, text, finish });
        return new(data.GetProperty("total").GetInt32(), data.GetProperty("offset").GetInt32(),
            data.GetProperty("cards").EnumerateArray().Select(MapCatalogCard).ToImmutableArray());
    }
    public async Task<DeckDetails> ReplacePrintingAsync(string deckId, long revision, string section, string cardId, string printingId) =>
        MapDeck(await Request("replacePrinting", new { deckId, revision, section, cardId, printingId }));
    public async Task<DeckDetails> EditDeckAsync(string deckId, long revision, ImmutableArray<DeckEdit> edits) =>
        MapDeck(await Request("edit", new { deckId, revision, edits = edits.Select(e => new { section = e.Section, cardId = e.CardId, quantity = e.Quantity }) }));
    public async Task<DeckDetails> RenameDeckAsync(string deckId, long revision, string name) => MapDeck(await Request("rename", new { deckId, revision, name }));
    public async Task<DeckDetails> DuplicateDeckAsync(string deckId, long revision, string name) => MapDeck(await Request("duplicate", new { deckId, revision, name }));
    public async Task<DeckDetails> UndoDeckAsync(string deckId, long revision) => MapDeck(await Request("undo", new { deckId, revision }));
    public async Task<DeckDetails> RedoDeckAsync(string deckId, long revision) => MapDeck(await Request("redo", new { deckId, revision }));
    public async Task<DeckDetails> SaveDeckAsync(string deckId, long revision) => MapDeck(await Request("save", new { deckId, revision }));
    public async Task<string> ExportDeckAsync(string deckId, long revision) => (await Request("export", new { deckId, revision, kind = "text" })).GetString() ?? "";
    private async Task<JsonElement> PrepareSolo(string deckId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deckId);
        // The document owner resolves an explicit deck without reopening an
        // already-current editor or trusting a native cache of prior responses.
        var setup = await Request("matchSetup", new { deckId });
        if (!string.Equals(Text(setup, "deckId"), deckId, StringComparison.Ordinal))
            throw new IOException("The rules adapter prepared a different deck than requested.");
        if (!setup.TryGetProperty("revision", out var revision) || revision.ValueKind != JsonValueKind.Number
            || !revision.TryGetInt64(out var preparedRevision) || preparedRevision < 0)
            throw new IOException("The rules adapter did not report the prepared deck revision.");
        var saveError = Text(setup, "saveError");
        if (saveError.Length > 0) throw new InvalidOperationException(saveError);
        var problem = Text(setup.GetProperty("setup"), "problem");
        if (problem.Length > 0) throw new InvalidOperationException(problem);
        return setup;
    }
    public async Task<SoloSetup> PrepareSoloAsync(string deckId)
    {
        var setup = await PrepareSolo(deckId);
        return MapSoloSetup(setup);
    }
    internal static SoloSetup MapSoloSetup(JsonElement setup) =>
        new(Text(setup, "deckId"), Text(setup, "name"), Read<ImmutableArray<AiOpponent>>(setup.GetProperty("opponents"))) {
            MaxPlayers = setup.GetProperty("maxPlayers").GetInt32(),
            DefaultOpponentIds = setup.TryGetProperty("defaultOpponentIds", out var defaults) && defaults.ValueKind != JsonValueKind.Null
                ? Read<ImmutableArray<string>>(defaults) : []
        };
    public async Task StartSoloAsync(string deckId, ImmutableArray<string> opponentIds)
    {
        if (opponentIds.IsDefaultOrEmpty || opponentIds.Length > 5) throw new ArgumentException("Choose between one and five AI opponents.", nameof(opponentIds));
        var setup = await PrepareSolo(deckId);
        if (opponentIds.Length >= setup.GetProperty("maxPlayers").GetInt32()) throw new ArgumentException("This format supports fewer players.", nameof(opponentIds));
        await Request("matchStart", new { deckId = Text(setup, "deckId"), revision = setup.GetProperty("revision").GetInt64(), opponents = opponentIds });
    }
    internal static Lobby MapLobby(JsonElement data)
    {
        try {
            var lobby = new Lobby(Text(data, "mode"), Text(data, "status"), Text(data, "error"),
                data.GetProperty("canStart").GetBoolean(), Text(data, "startProblem"), data.GetProperty("matchActive").GetBoolean(),
                Text(data, "portMapping"), Text(data, "internetInvite"), Read<ImmutableArray<Address>>(data.GetProperty("addresses")),
                data.GetProperty("slots").EnumerateArray().Select(MapLobbySeat).ToImmutableArray(), Text(data, "tableId")) {
                AiOpponents = data.TryGetProperty("aiOpponents", out var opponents) ? Read<ImmutableArray<AiOpponent>>(opponents) : [],
                Tempo = data.TryGetProperty("tempo", out var tempo) ? Read<TempoSettings>(tempo) : null,
                Revision = data.TryGetProperty("revision", out var revision) && revision.ValueKind == JsonValueKind.Number
                    && revision.TryGetInt64(out var value) ? Math.Max(0, value) : 0,
                MatchSetupCapabilities = OptionalLobbyFact<MatchSetupCapabilities>(data, "matchSetupCapabilities"),
                MatchSetup = OptionalLobbyFact<MatchSetupSpecification>(data, "matchSetup"),
                MatchSetupConsent = data.TryGetProperty("matchSetupConsent", out var consent) && consent.ValueKind != JsonValueKind.Null ? consent.GetString() : null,
                PartyProgress = OptionalLobbyFact<PartyProgress>(data, "partyProgress"),
                EncounterStartSupported = data.TryGetProperty("encounterStartSupported", out var supported) && supported.GetBoolean(),
                StartState = OptionalLobbyFact<EncounterStartState>(data, "startState")
            };
            if ((lobby.MatchSetupCapabilities != null || lobby.MatchSetup != null || lobby.PartyProgress != null
                    || lobby.EncounterStartSupported || lobby.StartState != null)
                && (!data.TryGetProperty("revision", out var exactRevision) || exactRevision.ValueKind != JsonValueKind.Number
                    || !exactRevision.TryGetInt64(out long exactValue) || exactValue < 0))
                throw new InvalidOperationException("Match setup requires an exact lobby revision.");
            MatchSetupFacts.Validate(lobby);
            if (lobby.PartyProgress is { } progress) EncounterFacts.Validate(progress);
            EncounterStartFacts.Validate(lobby);
            return lobby;
        } catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException or KeyNotFoundException) {
            throw new IOException("The rules provider published an invalid lobby snapshot.", error);
        }
    }
    private static T? OptionalLobbyFact<T>(JsonElement data, string name) where T : class
        => !data.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null
            : Read<T>(value);
    private static Seat MapLobbySeat(JsonElement data)
    {
        var seat = new Seat(data.GetProperty("index").GetInt32(), Text(data, "name"), Text(data, "type"),
            data.TryGetProperty("deck", out var deck) && deck.ValueKind == JsonValueKind.String ? deck.GetString() : null,
            data.GetProperty("ready").GetBoolean(), data.GetProperty("local").GetBoolean()) {
                OpponentId = data.TryGetProperty("opponentId", out var opponent) && opponent.ValueKind == JsonValueKind.String ? opponent.GetString() : null,
                ParticipantId = data.TryGetProperty("participantId", out var participant) && participant.ValueKind == JsonValueKind.String ? participant.GetString() ?? "" : "",
                TeamId = data.TryGetProperty("teamId", out var team) && team.ValueKind != JsonValueKind.Null ? team.GetInt32() : null,
                Role = data.TryGetProperty("role", out var role) && role.ValueKind != JsonValueKind.Null ? Read<MatchSeatRole>(role) : null
            };
        if (!data.TryGetProperty("deckPreview", out var preview) || preview.ValueKind != JsonValueKind.Object) return seat;
        var commanders = preview.TryGetProperty("commanders", out var faces) && faces.ValueKind == JsonValueKind.Array
            ? faces.EnumerateArray().Select(MapCatalogFace).ToImmutableArray() : [];
        var identity = new LobbyDeckPreview(Text(preview, "selectionId"), Text(preview, "name"), commanders,
            preview.TryGetProperty("problem", out var problem) && problem.ValueKind == JsonValueKind.String ? problem.GetString() : null);
        return seat with { Deck = identity.Name, DeckPreview = identity };
    }
    public bool SupportsTempo => current?.TempoAvailable == true;
    public async Task<Lobby> ConfigureTempoAsync(string tableId, TempoPreset preset, TempoExpiry expiry) =>
        MapLobby(await Request("multiplayerTempo", new { tableId, preset = preset.ToString().ToLowerInvariant(), expiry = expiry.ToString().ToLowerInvariant() }));
    public async Task SetReadyAsync(string tableId, bool ready, string tempoRevision) =>
        await Request("multiplayerReady", new { tableId, ready, tempoRevision });
    public async Task PresentedAsync(string gameId, string clockId) =>
        await Request("matchTempoPresented", new { sessionId = gameId, clockId });
    public async Task<TableConversation> ConversationAsync(string tableId) => Read<TableConversation>(await Request("tableSocial", new { tableId }));
    public async Task<TableConversation> SendMessageAsync(string tableId, string kind, string text) => Read<TableConversation>(await Request("tableChat", new { tableId, kind, text }));
    public async Task<TableConversation> SetPlayerNameAsync(string tableId, string name) => Read<TableConversation>(await Request("tableName", new { tableId, name }));
    public async Task<Lobby> HostAsync(int players, bool automaticPortForwarding) => MapLobby(await Request("multiplayerHost", new { format = "Commander", playerCount = players, autoPortForward = automaticPortForwarding }));
    public async Task<Lobby> JoinAsync(string address) => MapLobby(await Request("multiplayerJoin", new { address }));
    public async Task<Lobby> LobbyAsync() => MapLobby(await Request("multiplayerState"));
    public async Task<Lobby> SelectDeckAsync(LobbyDeckSelection selection) =>
        MapLobby(await Request("multiplayerSelectDeck", new { tableId = selection.TableId, deckId = selection.DeckId, libraryToken = selection.LibraryToken }));
    public async Task SetReadyAsync(bool ready) => await Request("multiplayerReady", new { ready });
    public async Task<Lobby> ConfigureSeatsAsync(string tableId, int players) =>
        MapLobby(await Request("multiplayerConfigure", new { tableId, format = "Commander", playerCount = players }));
    public async Task<Lobby> SetSeatAsync(string tableId, int seat, string type, string? opponentId = null) =>
        MapLobby(await Request("multiplayerSetSeat", new { tableId, seat, type, opponentId }));
    public async Task ReturnToLobbyAsync() => await Request("multiplayerReturn");
    public async Task LeaveAsync() => await Request("multiplayerClose");
    public async Task ConcedeAsync(string gameId) => await Request("matchConcede", new { sessionId = gameId });
    public async Task<GameSnapshot?> ObserveAsync()
    {
        var owner = current ?? throw new IOException("The engine is not running");
        long generation;
        ForgeCommandScope.ObservationLease read;
        lock (worldScopeGate) {
            generation = worldGeneration; read = owner.Commands.CaptureRead();
        }
        var raw = await Request("matchState", owner.PrivateActionFacesAvailable ? new { privateActionFaces = true } : null);
        lock (worldScopeGate) {
            if (current != owner || owner.Failure != null || worldGeneration != generation)
                throw new IOException("The observation connection or match changed.");
            owner.Commands.EnsureCurrentRead(read, raw.ValueKind == JsonValueKind.Null);
            GameSnapshot? state;
            try { state = ForgeSnapshotDecoder.Decode(raw, certifiedUndo: owner.CertifiedUndoAvailable); }
            catch {
                // A malformed retired read cannot erase a newer publication's
                // command authority, even when its audience stayed the same.
                owner.Commands.ClearIfCurrent(read);
                throw;
            }
            // Absence carries no revision. Only the exact captured publication
            // can be cleared by an in-flight observation returning null.
            owner.Commands.AdmitRead(read, state);
            return state;
        }
    }
    public async Task ExecuteAsync(GameCommand command)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var owner = current ?? throw new IOException("The engine is not running");
        if (command is UndoAction && !owner.CertifiedUndoAvailable)
            throw new EngineCommandRejectedException("This engine does not support certified gameplay Undo.");
        if (command is GrantPriorityContinuation or RevokePriorityContinuation or AcknowledgePriorityObservation
            && !owner.PriorityContinuationAvailable)
            throw new EngineCommandRejectedException("This engine does not support server-owned priority continuation.");
        var args = owner.Commands.Encode(command);
        if (current != owner || owner.Failure != null) throw new IOException("The engine is not running");
        await Request("matchAction", args);
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (current is not { } owner) return;
        owner.Commands.Clear();
        Fail(owner, new ObjectDisposedException(nameof(ForgeEngine)));
        try { await StopAsync(owner); }
        finally { if (current == owner) current = null; }
    }
    private static Task StopAsync(EngineProcess owner)
    {
        lock (owner) return owner.Stopped ??= StopProcessAsync(owner);
    }
    private static async Task StopProcessAsync(EngineProcess owner)
    {
        var process = owner.Process;
        try {
            // A partial framed write cannot be drained safely. End only that
            // broken owner before closing a writer the OS may still hold.
            if (owner.FramingFailed && !process.HasExited) {
                try { process.Kill(true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            try { process.StandardInput.Close(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
            if (!process.HasExited) {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) {
                    try { process.Kill(true); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
            }
            if (owner.Reader != null) await owner.Reader;
            if (owner.Diagnostics != null) await owner.Diagnostics;
        } finally { process.Dispose(); }
    }
}
