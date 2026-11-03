namespace Mana.Contracts;

public enum EngineRequestPhase { Ready, WriteGate, Writing, Flushing, Reply }

// The message is safe for ordinary connection UI. Only bounded operation/phase
// codes join the private diagnostic chain; request arguments and card data do not.
public sealed class EngineRequestTimeoutException : TimeoutException
{
    public string Operation { get; }
    public EngineRequestPhase Phase { get; }

    public EngineRequestTimeoutException(string operation, EngineRequestPhase phase, Exception? inner = null)
        : base("The rules engine did not answer in time.", inner)
    {
        if (operation is not ("engine-request" or "automation-enable" or "automation-observe" or "automation-execute" or "automation-fault"))
            throw new ArgumentException("Use a stable request operation code.", nameof(operation));
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        Operation = operation; Phase = phase;
    }

    public override string ToString() => base.ToString() + Environment.NewLine + "Request operation: " + Operation + "; phase: " + Phase + ".";
}
