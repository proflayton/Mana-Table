using System.Text.Json;
using System.Text.Json.Serialization;
using Mana.AI;
using Mana.Contracts;

namespace Mana.Table;

internal static class AiOwnLifeChoiceGradeChecks
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    internal static void Run(Action<bool, string> check)
    {
        foreach (var (name, drawIndex, otherIndex) in new[] {
            ("protected-12-4", 17, 41), ("renamed-protected-12-4", 93, 7), ("high-life-40-4", 17, 41)
        }) {
            var frame = Frame(Read(name));
            var good = Grade(frame, "prefer-opponent-draw", Command(frame, drawIndex));
            var wrong = Grade(frame, "prefer-opponent-draw", Command(frame, otherIndex));
            check(good["eligible"]!.GetValue<bool>() && good["grade"]!.ToString() == "pass"
                    && good["evaluator"]!.ToString() == AiExperimentReplay.OwnLifeChoiceEvaluator
                    && wrong["eligible"]!.GetValue<bool>() && wrong["admissible"]!.GetValue<bool>() && wrong["grade"]!.ToString() == "fail",
                "A frozen authored Draw preference judges structural current options independently of names, candidate scores or life threshold: " + name);
            foreach (var command in new DecisionCommand[] {
                Command(frame, drawIndex) with { DecisionId = "old-choice" }, Command(frame, 999),
                new ChooseOptions(frame.State.Id, frame.State.Decision!.Id, []),
                new ChooseOptions(frame.State.Id, frame.State.Decision!.Id, [drawIndex, drawIndex])
            }) {
                var stale = Grade(frame, "prefer-opponent-draw", command);
                check(stale["eligible"]!.GetValue<bool>() && !stale["admissible"]!.GetValue<bool>() && stale["grade"]!.ToString() == "fail",
                    "Stale, absent, duplicate and empty answers cannot gain a life-choice preference grade");
            }
            var exhausted = AiExperimentReplay.EvaluateGrade(frame, "prefer-opponent-draw",
                new("budget-control", "arbitrary-policy-id", AiSeatStatus.BudgetExceeded, null, null));
            check(exhausted["eligible"]!.GetValue<bool>() && exhausted["grade"]!.ToString() == "fail",
                "A candidate cutoff retains the full original authored preference opportunity denominator");
        }
        foreach (int amount in new[] { 0, 1 }) {
            var frame = Frame(Read("known-self-loss-" + amount));
            check(Grade(frame, "prefer-fixed-self-loss", Command(frame, 41))["grade"]!.ToString() == "pass"
                    && Grade(frame, "prefer-fixed-self-loss", Command(frame, 17))["grade"]!.ToString() == "fail"
                    && !Grade(frame, "prefer-opponent-draw", Command(frame, 17))["eligible"]!.GetValue<bool>(),
                "The known zero/one-life comparator has its own authored preference and cannot qualify as unknown loss");
        }
        var original = Read("protected-12-4");
        var noCertificate = original with { Semantics = original.Semantics with { Options = [original.Semantics.Options[0],
            original.Semantics.Options[1] with { RememberedMillLifeLoss = null }] } };
        var unknown = Frame(noCertificate);
        check(!Grade(unknown, "prefer-opponent-draw", Command(unknown, 17))["eligible"]!.GetValue<bool>()
                && Grade(unknown, "prefer-opponent-draw", Command(unknown, 17))["grade"]!.ToString() == "ungraded",
            "Missing old-wire mechanical evidence is explicitly incompatible/ungraded, not a fabricated preference pass");
        var certified = Frame(original);
        check(!Grade(certified, "prefer-fixed-self-loss", Command(certified, 41))["eligible"]!.GetValue<bool>(),
            "An unknown aggregate cannot gain the known-bounded-loss endpoint through an option label");
    }

    private static System.Text.Json.Nodes.JsonObject Grade(AiDecisionFrame frame, string kind, DecisionCommand command)
        => AiExperimentReplay.EvaluateGrade(frame, kind, new("authored-preference-control", "arbitrary-policy-id", AiSeatStatus.Ready, command, null));
    private static ChooseOptions Command(AiDecisionFrame frame, int index) => new(frame.State.Id, frame.State.Decision!.Id, [index]);
    private static AiDecisionFrame Frame(AiDecisionReplayCase replay) => new(replay.State, domain: replay.Domain, semantics: replay.Semantics);
    private static AiDecisionReplayCase Read(string name) => JsonSerializer.Deserialize<AiDecisionReplayCase>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PunisherChoiceAuthored", name + ".json")), Json)!;
}
