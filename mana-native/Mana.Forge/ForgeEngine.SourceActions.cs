using Mana.Contracts;
using Mana.Planning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mana.Forge;

public sealed partial class ForgeEngine
{
    private static readonly JsonSerializerOptions SourceActionJson = new() { PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };

    internal static SourceActionSet DecodeSourceActions(JsonElement reply)
    {
        try { return reply.Deserialize<SourceActionSet>(SourceActionJson)
            ?? throw new IOException("The rules adapter returned an empty source action list."); }
        catch (JsonException error) { throw new IOException("The rules adapter returned an invalid source action schema.", error); }
    }

    private string sourceActionsUnsupportedGame = "";
    public bool SupportsSourceActions => current is { SourceActionsAvailable: true, Failure: null } owner
        && owner.Ready.Task.IsCompletedSuccessfully && !disposed
        && owner.Commands.ObservedGameId is { Length: > 0 } game
        && (sourceActionsUnsupportedGame.Length == 0 || sourceActionsUnsupportedGame != game);

    public async Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCardKey);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!SupportsSourceActions) throw new NotSupportedException("This rules adapter does not expose source actions.");
        var owner = current ?? throw new IOException("The engine is not running");
        if (!owner.Commands.CanReadSource(gameId, decisionId, sourceCardKey))
            throw new EngineCommandRejectedException("That source does not belong to the currently observed decision.");
        var observed = owner.Commands.ObservedState ?? throw new EngineCommandRejectedException("Observe the current source first.");
        SourceActionSet result;
        try {
            result = DecodeSourceActions(await Request("matchSourceActions", new {
                sessionId = gameId, promptId = decisionId, key = sourceCardKey, expectedRevision = observed.Revision
            }));
        } catch (EngineCommandRejectedException error) when (error.Message is
            "Unknown synchronization command" or "Unknown seat command" or "Unknown engine command") {
            // A current local process can connect to an older table host. Only
            // an explicit unsupported command revokes this optional capability.
            sourceActionsUnsupportedGame = gameId;
            throw new NotSupportedException("This table host does not expose source actions.", error);
        }
        if (current != owner || owner.Failure != null || !owner.Commands.CanReadSource(gameId, decisionId, sourceCardKey)
            || owner.Commands.ObservedState is not { } latest || latest.Revision != observed.Revision || latest.ViewerId != observed.ViewerId)
            throw new EngineCommandRejectedException("The source action context changed. Select the current card again.");
        if (!string.Equals(result.GameId, gameId, StringComparison.Ordinal)
            || !string.Equals(result.DecisionId, decisionId, StringComparison.Ordinal)
            || !string.Equals(result.SourceCardKey, sourceCardKey, StringComparison.Ordinal)
            || result.Revision is { } revision && revision != observed.Revision)
            throw new EngineCommandRejectedException("The source action context changed. Select the current card again.");
        if (result.SourceCardId == null || result.Actions.IsDefault || result.Actions.Any(action => action == null || string.IsNullOrWhiteSpace(action.Id)
                || action.Kind is not ("activate" or "cast" or "play") || string.IsNullOrWhiteSpace(action.Text)
                || action.Card is { } linked && (linked.FaceDown || linked.Key != "" || linked.Selectable || linked.Highlighted
                    || string.IsNullOrWhiteSpace(linked.Name) || string.IsNullOrWhiteSpace(action.Zone)))
            || result.Actions.Select(action => action.Id).Distinct(StringComparer.Ordinal).Count() != result.Actions.Length)
            throw new IOException("The rules adapter returned an invalid source action list.");
        try {
            foreach (var action in result.Actions.Where(action => action.CostPreview != null)) {
                if (result.Revision == null || !Enum.IsDefined(action.CostPreview!.ManaCostKnowledge))
                    throw new ArgumentException("A cost preview requires a bound source revision and knowledge.");
                ManaFundingFacts.ValidateCosts(action.CostPreview.Costs);
                ManaFundingFacts.Validate(observed, action.CostPreview.Funding);
            }
        } catch (ArgumentException error) { throw new IOException("The rules adapter returned unscoped funding facts.", error); }
        return result;
    }
}
