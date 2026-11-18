using System.Diagnostics;

namespace Mana.Table;

// Development-only entry points. These never enter the ordinary player profile.
internal static class AiExperimentCommands
{
    internal static bool Handles(string[] args) => args.Any(value => value is
        "--ai-experiment-plan" or "--ai-experiment-run" or "--ai-experiment-analyze" or "--ai-experiment-worker");

    internal static int Run(string[] args)
    {
        if (args.Contains("--ai-experiment-worker")) {
            var workerFlags = new HashSet<string>(StringComparer.Ordinal) {
                "--ai-experiment-worker", "--job-id", "--attempt-directory", "--reservation-token"
            };
            if (args.Length != 8 || args.Where((_, index) => index % 2 == 0).Any(value => !workerFlags.Remove(value))
                || workerFlags.Count != 0) throw new ArgumentException("A worker requires exactly its four reserved arguments.");
            return AiExperimentWorker.RunAsync(args).GetAwaiter().GetResult();
        }
        string flag = args.Single(value => value is "--ai-experiment-plan" or "--ai-experiment-run" or "--ai-experiment-analyze");
        string operation = flag["--ai-experiment-".Length..];
        var allowed = new HashSet<string>(StringComparer.Ordinal) { flag };
        if (operation == "plan") allowed.Add("--profile");
        if (operation == "run") { allowed.Add("--jobs"); allowed.Add("--max-jobs"); }
        if (args.Length % 2 != 0 || args.Where((_, index) => index % 2 == 0).Any(value => !allowed.Contains(value)))
            throw new ArgumentException("Unsupported experiment command argument.");
        string input = Argument(args, flag);
        string script = Path.Combine(AppContext.BaseDirectory, "ExperimentTools", "ai-experiment.cjs");
        if (!File.Exists(script)) throw new FileNotFoundException("Build the complete experiment harness, including its coordinator tools.", script);
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(script); start.ArgumentList.Add(operation); start.ArgumentList.Add(input);
        if (operation == "plan") {
            start.ArgumentList.Add("--profile"); start.ArgumentList.Add(Argument(args, "--profile"));
        } else if (operation == "run") {
            start.ArgumentList.Add("--jobs"); start.ArgumentList.Add(Optional(args, "--jobs") ?? "1");
            if (Optional(args, "--max-jobs") is { } maximum) {
                start.ArgumentList.Add("--max-jobs"); start.ArgumentList.Add(maximum);
            }
        }
        using var process = Process.Start(start) ?? throw new IOException("Could not start the experiment coordinator.");
        process.WaitForExit();
        return process.ExitCode;
    }

    internal static string Argument(string[] args, string name) => Optional(args, name)
        ?? throw new ArgumentException("Missing experiment argument: " + name);
    private static string? Optional(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (Array.LastIndexOf(args, name) != index || index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("An experiment argument requires one value: " + name);
        return args[index + 1];
    }
}
