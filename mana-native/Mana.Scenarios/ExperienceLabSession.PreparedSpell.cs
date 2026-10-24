using Mana.Contracts;

namespace Mana.Scenarios;

public sealed partial class ExperienceLabSession
{
    private int preparedRecipient;
    private bool preparedAgain;

    private SourceActionSet PreparedSpellActions(Card source)
    {
        SourceAction[] actions = [];
        if (StageIndex is 0 or 5 && State.Decision?.Intent == DecisionIntent.Priority && !source.FaceDown) {
            var spell = State.Viewer!.Zone("Exile").Cards.Single();
            if (source.VisualId == PreparedSpellScenario.SourceId || source.VisualId == spell.VisualId)
                actions = [new(State.Decision.Id + ":" + source.Key + ":" + PreparedSpellScenario.CastAction, "cast", "Ancestral Recall — {U}") {
                    Card = source.VisualId == PreparedSpellScenario.SourceId ? spell with { Key = "", Selectable = false } : null,
                    Zone = source.VisualId == PreparedSpellScenario.SourceId ? "Exile" : ""
                }];
        }
        return new(State.Id, State.Decision!.Id, source.Key, source.VisualId, [.. actions]);
    }

    private GameSnapshot ReplyPreparedSpell(DecisionCommand reply)
    {
        if (StageIndex == 1) {
            if (reply is CancelSelection) return AdvancePreparedSpell(0);
            if (reply is SelectPlayer player && State.Decision!.PlayerChoices.Contains(player.Player)) {
                preparedRecipient = player.Player;
                return AdvancePreparedSpell(2);
            }
        }
        if (StageIndex == 2 && reply is PassPriority) return AdvancePreparedSpell(3);
        throw Unsupported();
    }

    private GameSnapshot AdvancePreparedSpell(int stage)
    {
        if (stage == 5 || StageIndex == 5) preparedAgain = true;
        StageIndex = stage;
        return State = Stamp(PreparedSpellScenario.Snapshot(stage, preparedRecipient, preparedAgain));
    }
}
