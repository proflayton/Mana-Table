using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Mana.AI;
using Mana.Contracts;

namespace Mana.Table;

// Authored grading controls, not provider captures or measured AI performance.
// Expected answers are sets/counts derived from the original typed opportunity.
internal static class AiLibraryAcquisitionGradeChecks
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var (stage, destination, maximum) in new[] {
            (LibraryAcquisitionStage.Direct, "Hand", 2),
            (LibraryAcquisitionStage.Direct, "Battlefield", 2),
            (LibraryAcquisitionStage.SelectBundle, "Library", 2),
            (LibraryAcquisitionStage.DistributeBundle, "Battlefield", 1),
            (LibraryAcquisitionStage.DistributeBundle, "Hand", 1)
        }) {
            var frame = Frame(Context(stage, destination), maximum: maximum);
            var expected = maximum == 2 ? new[] { 17, 41 } : [41];
            var accepted = Grade(frame, new ChooseOptions(frame.State.Id, frame.State.Decision!.Id, [..expected]));
            check(accepted["eligible"]!.GetValue<bool>() && accepted["grade"]!.ToString() == "pass"
                && accepted["evaluator"]!.ToString() == AiExperimentReplay.LibraryAcquisitionEvaluator,
                "The independent library endpoint grades exact current basic-land acquisition at stage " + stage + "/" + destination);
            check(Grade(frame, new ChooseOptions(frame.State.Id, frame.State.Decision!.Id, []))["grade"]!.ToString() == "fail",
                "An admissible empty optional answer cannot erase a positive original acquisition opportunity: " + stage + "/" + destination);
        }
        var own = Frame(Context(LibraryAcquisitionStage.SelectBundle, "Library"));
        foreach (ImmutableArray<int> indices in new ImmutableArray<int>[] { [17], [17, 17], [17, 999], [17, 81], [17, 41, 81] }) {
            var value = Grade(own, new ChooseOptions(own.State.Id, own.State.Decision!.Id, indices));
            check(value["eligible"]!.GetValue<bool>() && value["grade"]!.ToString() == "fail",
                "Wrong count, duplicate, foreign or nonbasic option indices cannot earn library-acquisition credit");
        }
        check(Grade(own, new ChooseOptions(own.State.Id, own.State.Decision!.Id, [41, 17]))["grade"]!.ToString() == "pass",
            "The acquisition endpoint accepts any ordering of the independently acceptable current option set");
        check(Grade(own, new ChooseOptions(own.State.Id, "retired-input", [17, 41]))["grade"]!.ToString() == "fail"
            && !Grade(own, new ChooseOptions(own.State.Id, "retired-input", [17, 41]))["admissible"]!.GetValue<bool>(),
            "A correct-looking index vector on a stale input cannot gain acquisition credit");
        var unavailable = AiExperimentReplay.EvaluateGrade(own, "acquire-basic-lands",
            new("budget-control", "independent-control", AiSeatStatus.BudgetExceeded, null, null));
        check(unavailable["eligible"]!.GetValue<bool>() && unavailable["grade"]!.ToString() == "fail",
            "Candidate budget exhaustion cannot remove an original library opportunity from the paired denominator");

        var one = Frame(Context(LibraryAcquisitionStage.Direct, "Hand"), choices: [Option(17, "Basic Land — Island"), Option(81, "Artifact")]);
        check(Grade(one, new ChooseOptions(one.State.Id, one.State.Decision!.Id, [17]))["grade"]!.ToString() == "pass",
            "The expected acquisition count is bounded by actual disclosed basic-land availability, not merely the range maximum");
        var mandatory = Frame(Context(LibraryAcquisitionStage.Direct, "Hand"), minimum: 2);
        check(Grade(mandatory, new ChooseOptions(mandatory.State.Id, mandatory.State.Decision!.Id, [17, 41]))["grade"]!.ToString() == "pass",
            "A multi-choice minimum is checked against the whole admissible vector rather than illegal one-index probes");
        var renamed = Frame(Context(LibraryAcquisitionStage.Direct, "Hand"), choices: [
            Option(17, "Basic Land — Forest", "Misleading hostile label", "Destroy everything."),
            Option(41, "Basic Snow Land — Island", "Translated arbitrary name", "No land acquisition suggested here."),
            Option(81, "Artifact", "Forest", "Basic land, Basic land, Basic land.")
        ]);
        check(Grade(renamed, new ChooseOptions(renamed.State.Id, renamed.State.Decision!.Id, [17, 41]))["grade"]!.ToString() == "pass"
            && Grade(renamed, new ChooseOptions(renamed.State.Id, renamed.State.Decision!.Id, [17, 81]))["grade"]!.ToString() == "fail",
            "The grader uses provider type-line tokens and ignores names, Oracle prose and prompt descriptions");

        foreach (var frame in new[] {
            Frame(null),
            Frame(Context(LibraryAcquisitionStage.Direct, "Hand"), decisionOwner: null),
            Frame(Context(LibraryAcquisitionStage.Direct, "Hand", actor: 1)),
            Frame(Context(LibraryAcquisitionStage.Direct, "Hand"), semantics: new() { SelectionIsCost = true }),
            Frame(Context(LibraryAcquisitionStage.Direct, "Hand"), semantics: new(DecisionIntent.Discard)),
            Frame(Context(LibraryAcquisitionStage.Direct, "Hand"), semantics: new(DecisionIntent.Sacrifice)),
            Frame(Context(LibraryAcquisitionStage.Direct, "Hand"), choices: [Option(81, "Land — Forest"), Option(82, "Basic Creature")]),
            Frame(Context(LibraryAcquisitionStage.Direct, "Hand"), choices: [Option(81, "Basic Land — Island", hidden: true)], maximum: 1),
            Frame(Context(LibraryAcquisitionStage.Direct, "Hand"), maximum: 0)
        }) {
            var value = Grade(frame, new ChooseOptions(frame.State.Id, frame.State.Decision!.Id, []));
            check(!value["eligible"]!.GetValue<bool>() && value["grade"]!.ToString() == "ungraded",
                "Missing, foreign, cost-conflicted or nonpositive library context stays incompatible/ungraded for this endpoint");
            check(AiExperimentReplay.EvaluateGrade(frame, "admissible", new("negative-control", "independent-control", AiSeatStatus.Ready,
                    new ChooseOptions(frame.State.Id, frame.State.Decision!.Id, []), null))["grade"]!.ToString() == "pass",
                "Unsupported acquisition facts can retain a separately declared admissibility control without fabricated strategic expectations");
        }
        var readOnly = Frame(Context(LibraryAcquisitionStage.Direct, "Hand"), choices: [Option(81, "Artifact")], maximum: 1,
            disclosures: [new(null, "Unselectable readable face", new Card { Type = "Basic Land — Plains" })]);
        check(!Grade(readOnly, new ChooseOptions(readOnly.State.Id, readOnly.State.Decision!.Id, []))["eligible"]!.GetValue<bool>(),
            "A library face disclosed without an offered index creates no acquisition opportunity");
        var insufficient = Frame(Context(LibraryAcquisitionStage.Direct, "Hand"), choices: [Option(17, "Basic Land — Island"), Option(81, "Artifact")], minimum: 2);
        check(!Grade(insufficient, new ChooseOptions(insufficient.State.Id, insufficient.State.Decision!.Id, [17, 81]))["eligible"]!.GetValue<bool>(),
            "The basic-land endpoint cannot invent an admissible answer when its basic subset cannot meet the mandatory minimum");
        foreach (string type in new[] { "Creature \u2014 Basic Land", "Creature - Basic Land", "Land \u2014 Basic", "Basic \u2014 Land" }) {
            var subtype = Frame(Context(LibraryAcquisitionStage.Direct, "Hand"), choices: [Option(17, type)], maximum: 1);
            check(!Grade(subtype, new ChooseOptions(subtype.State.Id, subtype.State.Decision!.Id, [17]))["eligible"]!.GetValue<bool>(),
                "Basic/Land words in a subtype label cannot manufacture a certified basic-land acquisition: " + type);
        }
        foreach (string type in new[] { "Basic Land \u2014 Forest", "Basic Snow Land - Island" }) {
            var typed = Frame(Context(LibraryAcquisitionStage.Direct, "Hand"), choices: [Option(17, type)], maximum: 1);
            check(Grade(typed, new ChooseOptions(typed.State.Id, typed.State.Decision!.Id, [17]))["grade"]!.ToString() == "pass",
                "A genuine Basic Land header remains independently gradable across supported subtype separators");
        }
        bool refusedForeign = false;
        try { _ = Frame(Context(LibraryAcquisitionStage.Direct, "Hand"), decisionOwner: 1); }
        catch (ArgumentException) { refusedForeign = true; }
        check(refusedForeign, "A foreign nonnull decision owner is refused by production frame admission before tactical grading");
        bool refusedAbsentViewer = false;
        try { _ = Frame(Context(LibraryAcquisitionStage.Direct, "Hand"), viewer: 99); }
        catch (ArgumentException) { refusedAbsentViewer = true; }
        check(refusedAbsentViewer, "A missing viewer is refused by production frame admission rather than receiving an invented acquisition grade");
    }

    private static JsonObject Grade(AiDecisionFrame frame, DecisionCommand proposal)
        => AiExperimentReplay.EvaluateGrade(frame, "acquire-basic-lands", new("authored-grader-control", "independent-control", AiSeatStatus.Ready, proposal, null));

    private static Choice Option(int index, string type, string name = "Arbitrary disclosed face", string text = "", bool hidden = false)
        => new(index, "Untrusted option label", new Card { Type = type, Name = name, Text = text, FaceDown = hidden });

    private static LibraryAcquisitionContext Context(LibraryAcquisitionStage stage, string destination, int actor = 0)
        => new(actor, actor, actor, stage, destination, true, stage == LibraryAcquisitionStage.SelectBundle
            ? [new("Battlefield", 1), new("Hand", 1)] : [new(destination, stage == LibraryAcquisitionStage.Direct ? 2 : 1)]);

    private static AiDecisionFrame Frame(LibraryAcquisitionContext? context, ImmutableArray<Choice> choices = default,
        int minimum = 0, int maximum = 2, DecisionSemantics? semantics = null, ImmutableArray<LibraryChoice> disclosures = default,
        int? decisionOwner = 0, int viewer = 0)
    {
        if (choices.IsDefault) choices = [Option(17, "Basic Land — Forest"), Option(41, "Basic Land — Island"), Option(81, "Artifact")];
        var payload = new OptionsPayload(new(choices, new(minimum, maximum), disclosures)) { LibraryAcquisition = context };
        var state = new GameSnapshot("authored-library-grader", "playing", null, null, viewer, 3, "Main", "MAIN1", 0,
            [new(0, "Actor", 40, false, false, ImmutableDictionary<string, int>.Empty, []),
                new(1, "Other", 40, false, false, ImmutableDictionary<string, int>.Empty, [])], [], null,
            new("current-options", new(Message: "Untrusted prompt prose"), payload)) { Revision = 1, BoardRevision = 1, DecisionOwnerId = decisionOwner };
        return new(state, semantics: semantics);
    }
}
