using Mana.Contracts;

namespace Mana.Client;

// Policies own their finite consent and current matching rules. The common
// runner owns only bounded serial delivery, observation and cancellation.
internal interface IFiniteDecisionPolicy
{
    bool IsActive { get; }
    bool IsPaused => false;
    bool CanContinue(GameSnapshot state);
    DecisionCommand? NextCommand(GameSnapshot state);
    void Submitted(GameSnapshot state, DecisionCommand command);
    void Stop();
}
