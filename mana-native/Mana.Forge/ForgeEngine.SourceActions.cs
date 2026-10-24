using Mana.Contracts;

namespace Mana.Forge;

public sealed partial class ForgeEngine
{
    private string sourceActionsGame = "", sourceActionsUnsupportedGame = "";
    public bool SupportsSourceActions => current is { SourceActionsAvailable: true, Failure: null } owner
        && owner.Ready.Task.IsCompletedSuccessfully && !disposed
        && (sourceActionsUnsupportedGame.Length == 0 || sourceActionsUnsupportedGame != sourceActionsGame);

    public async Task<SourceActionSet> SourceActionsAsync(string gameId, string decisionId, string sourceCardKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCardKey);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!SupportsSourceActions) throw new NotSupportedException("This rules adapter does not expose source actions.");
        SourceActionSet result;
        try {
            result = Read<SourceActionSet>(await Request("matchSourceActions", new {
                sessionId = gameId, promptId = decisionId, key = sourceCardKey
            }));
        } catch (EngineCommandRejectedException error) when (error.Message is
            "Unknown synchronization command" or "Unknown seat command" or "Unknown engine command") {
            // A current local process can connect to an older table host. Only
            // an explicit unsupported command revokes this optional capability.
            sourceActionsUnsupportedGame = gameId;
            throw new NotSupportedException("This table host does not expose source actions.", error);
        }
        if (!string.Equals(result.GameId, gameId, StringComparison.Ordinal)
            || !string.Equals(result.DecisionId, decisionId, StringComparison.Ordinal)
            || !string.Equals(result.SourceCardKey, sourceCardKey, StringComparison.Ordinal))
            throw new EngineCommandRejectedException("The source action context changed. Select the current card again.");
        if (result.SourceCardId == null || result.Actions.IsDefault || result.Actions.Any(action => action == null || string.IsNullOrWhiteSpace(action.Id)
                || action.Kind is not ("activate" or "cast" or "play") || string.IsNullOrWhiteSpace(action.Text)
                || action.Card is { } linked && (linked.FaceDown || linked.Key != "" || linked.Selectable || linked.Highlighted
                    || string.IsNullOrWhiteSpace(linked.Name) || string.IsNullOrWhiteSpace(action.Zone)))
            || result.Actions.Select(action => action.Id).Distinct(StringComparer.Ordinal).Count() != result.Actions.Length)
            throw new IOException("The rules adapter returned an invalid source action list.");
        return result;
    }
}
