using Mana.AI;

namespace Mana.Table;

// Local host diagnostics may contain exception details. They never cross the
// public match port or enter another seat's policy frame.
internal sealed class AiHostDiagnostics(string profile) : IAiHostDiagnostics
{
    private readonly object gate = new();
    public void Report(AiHostFault fault, Exception? error)
    {
        lock (gate) {
            string directory = Path.Combine(profile, "diagnostics");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "ai-host.log"),
                $"{DateTimeOffset.UtcNow:O} game={fault.GameId} seat={fault.SeatId} reason={fault.ReasonCode}{Environment.NewLine}"
                + (error == null ? "" : error + Environment.NewLine));
        }
    }
}
