using System.Collections.Immutable;
using Mana.Contracts;

namespace Mana.Scenarios;

public sealed partial class ExperienceLabSession
{
    private SourceActionSet PlayAccessActions(Card source)
    {
        SourceAction[] actions = [];
        if (State.Decision?.Payload is PriorityPayload && State.DecisionOwnerId == State.ViewerId
            && State.PriorityPlayerId == State.ViewerId && !source.FaceDown) {
            if (State.Viewer!.Zone("Hand").Cards.Contains(source) && source.Selectable)
                actions = [new(State.Decision.Id + ":" + source.Key + ":hand", HandIsLand(source.VisualId) ? "play" : "cast",
                    HandIsLand(source.VisualId) ? "Play land" : "Cast " + source.Name)];
            else if (State.PlayAccesses.SingleOrDefault(item => item.CardId == source.VisualId) is { } access)
                actions = access.Routes.Where(route => route.CanStart).Select(route => new SourceAction(
                    State.Decision.Id + ":" + source.Key + ":access:" + route.Id,
                    route.Kind == PlayAccessKind.PlayLand ? "play" : "cast",
                    route.Detail + (access.Routes.Length > 1 ? " — " + route.SourceName : ""))).ToArray();
        }
        return new(State.Id, State.Decision!.Id, source.Key, source.VisualId, actions.ToImmutableArray());
    }

    private GameSnapshot UsePlayAccessAction(SourceActionSet offered, SourceAction selected)
    {
        if (State.Viewer!.Zone("Hand").Cards.SingleOrDefault(card => card.VisualId == offered.SourceCardId) is { } hand) {
            if (HandIsLand(hand.VisualId)) {
                StageIndex = 10;
                return State = Stamp(PlayAccessScenario.LandFrom(State, State.ViewerId, "Hand", hand));
            }
            StageIndex = 6;
            return State = Stamp(PlayAccessScenario.CastFrom(State, State.ViewerId, "Hand", hand));
        }
        var access = State.PlayAccesses.SingleOrDefault(item => item.CardId == offered.SourceCardId) ?? throw Unsupported();
        var route = access.Routes.SingleOrDefault(item => selected.Id == State.Decision!.Id + ":" + offered.SourceCardKey + ":access:" + item.Id)
            ?? throw Unsupported();
        if (!route.CanStart) throw Unsupported();
        if (route.Kind == PlayAccessKind.PlayLand) {
            StageIndex = 10;
            return State = Stamp(PlayAccessScenario.Land(State, access.CardId));
        }
        StageIndex = access.CardId == PlayAccessScenario.FlashbackId ? 8 : 6;
        return State = Stamp(PlayAccessScenario.Cast(State, access.CardId));
    }

    private GameSnapshot ReplyPlayAccess(DecisionCommand reply)
    {
        if (reply is not PassPriority || State.Decision?.Payload is not PriorityPayload || StageIndex is not (6 or 8)
            || State.Stack.Length != 1 || State.Stack[0].Card is not { } spell)
            throw Unsupported();
        string id = spell.VisualId;
        string destination = id switch {
            PlayAccessScenario.ExileSpellId or PlayAccessScenario.BorrowedId => "Battlefield",
            PlayAccessScenario.FlashbackId => "Exile",
            _ => AuthoredHandIndex(id) is 2 or 4 or 5 or 9 or 10 ? "Battlefield" : "Graveyard"
        };
        StageIndex = id == PlayAccessScenario.FlashbackId ? 9 : 7;
        return State = Stamp(PlayAccessScenario.Resolve(State, id, destination));
    }

    private static bool HandIsLand(string id) => AuthoredHandIndex(id) is 0 or 1 or 6 or 7 or 8;

    // The authored hand repeats eleven explicitly declared faces. This maps its
    // fixture occurrence to that declaration; it does not inspect card rules.
    private static int AuthoredHandIndex(string id) => id.StartsWith("access-hand-", StringComparison.Ordinal)
        && int.TryParse(id.AsSpan("access-hand-".Length), out int index) && index >= 0 ? index % 11 : -1;
}
