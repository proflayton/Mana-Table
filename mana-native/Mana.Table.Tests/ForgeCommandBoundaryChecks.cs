using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Forge;
using System.Text.Json.Nodes;

namespace Mana.Table;

// Uses the exact encoder/observation gate used by ForgeEngine.ExecuteAsync.
// These are transport and permission checks, not Magic rules simulations.
internal static class ForgeCommandBoundaryChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var selection = new JsonObject { ["kind"] = "input", ["task"] = "selection", ["purpose"] = "targets",
            ["inputType"] = "UnrelatedProviderImplementation", ["context"] = "private-provider-metadata" };
        ForgeDecisionDecoder.Normalize(selection);
        check(selection["intent"]!.GetValue<int>() == (int)DecisionIntent.Selection
            && selection["purpose"]!.GetValue<int>() == (int)DecisionPurpose.TargetSelection
            && !selection.ContainsKey("inputType") && !selection.ContainsKey("context") && !selection.ContainsKey("task"),
            "The adapter consumes authoritative semantic tasks without parsing or exposing provider class names or private context");
        foreach (var invalid in new[] {
            new JsonObject { ["kind"] = "input", ["task"] = "unknown-required-task" },
            new JsonObject { ["kind"] = "choice", ["task"] = "mana" },
            new JsonObject { ["kind"] = "input", ["task"] = "openingHand", ["purpose"] = "targets" },
            new JsonObject { ["kind"] = "input", ["task"] = "mana", ["priorityAlternative"] = "undo" }
        }) {
            bool failed = false;
            try { ForgeDecisionDecoder.Normalize(invalid); } catch (IOException) { failed = true; }
            check(failed, "An unsupported or contradictory provider decision fails explicitly rather than becoming a generic confirmation");
        }
        var scope = new ForgeCommandScope();
        var source = new Card { VisualId = "visible-source", Key = "prompt:source", Name = "Source", Selectable = true };
        GameSnapshot State(Decision decision) => new("observed-game", "playing", null, null, 0, 2, "Main phase", "MAIN1", 1,
            [new(0, "You", 40, true, false, ImmutableDictionary<string, int>.Empty, [new("Battlefield", 1, [source], null)])], [], null,
            decision with { Id = "observed-prompt", Kind = decision.Kind.Length == 0 ? "input" : decision.Kind });
        void Reject(GameCommand command, string description)
        {
            bool rejected = false;
            try { scope.Encode(command); } catch (EngineCommandRejectedException) { rejected = true; }
            check(rejected, description);
        }
        const string game = "observed-game", prompt = "observed-prompt";
        Reject(new KeepOpeningHand(game, prompt), "The Forge command boundary requires an observation before any decision can be translated");

        var opening = State(new() { Kind = "input", Purpose = DecisionPurpose.OpeningHand, OkEnabled = true, CancelEnabled = true });
        scope.Observe(opening);
        var keep = scope.Encode(new KeepOpeningHand(game, prompt));
        check((string)keep["expectedTask"]! == "openingHand" && (string)keep["action"]! == "ok",
            "Keeping an observed opening hand carries its semantic task alongside the exact game and prompt");
        Reject(new PayMana(game, prompt), "A scoped mana command cannot consume an opening hand through the shared ok wire action");
        Reject(new PassPriority(game, prompt), "A scoped priority command cannot consume an opening hand");
        Reject(new KeepOpeningHand("another-game", prompt), "The adapter rejects another game's otherwise matching opening-hand command");
        Reject(new KeepOpeningHand(game, "retired-prompt"), "The adapter rejects a retired prompt before writing a wire command");

        var mana = State(new() { Kind = "input", Intent = DecisionIntent.Mana, OkEnabled = true, CancelEnabled = true });
        scope.Observe(mana);
        var payment = scope.Encode(new PayMana(game, prompt));
        check((string)payment["expectedTask"]! == "mana" && (string)payment["action"]! == "ok"
            && (string)payment["sessionId"]! == game && (string)payment["promptId"]! == prompt,
            "The same wire ok carries the observed mana task rather than borrowing an opening-hand meaning");
        Reject(new KeepOpeningHand(game, prompt), "An opening-hand command cannot pay a current mana cost even when its scope strings match");
        var manaSource = scope.Encode(new UseManaSource(game, prompt, source.Key));
        check((string)manaSource["expectedTask"]! == "mana" && (string)manaSource["key"]! == source.Key,
            "A mana-source gesture keeps its current disclosed card handle and semantic payment task");

        (Decision Decision, GameCommand Command, string Task)[] acknowledgements = [
            (new() { Intent = DecisionIntent.Attack, OkEnabled = true }, new CompleteAttackers(game, prompt), "attack"),
            (new() { Intent = DecisionIntent.Block, OkEnabled = true }, new CompleteBlockers(game, prompt), "block"),
            (new() { Intent = DecisionIntent.Selection, OkEnabled = true }, new CompleteSelection(game, prompt), "selection"),
            (new() { Intent = DecisionIntent.General, OkEnabled = true, CancelEnabled = true }, new AcceptConfirmation(game, prompt), "confirmation"),
            (new() { Intent = DecisionIntent.General, OkEnabled = true }, new AcknowledgeDecision(game, prompt), "confirmation"),
            (new() { Intent = DecisionIntent.Selection, CancelEnabled = true }, new CancelSelection(game, prompt), "selection"),
            (new() { Intent = DecisionIntent.Mana, CancelEnabled = true }, new CancelPayment(game, prompt), "mana"),
            (new() { Intent = DecisionIntent.General, CancelEnabled = true }, new DeclineConfirmation(game, prompt), "confirmation")
        ];
        foreach (var row in acknowledgements) {
            scope.Observe(State(row.Decision));
            var encoded = scope.Encode(row.Command);
            check((string)encoded["expectedTask"]! == row.Task,
                row.Command.GetType().Name + " is translated only for its currently observed semantic task");
            Reject(new KeepOpeningHand(game, prompt), row.Command.GetType().Name + " cannot be replaced with another task's scoped ok meaning");
        }

        var priority = State(new() { Intent = DecisionIntent.Priority, OkEnabled = true, CancelEnabled = true,
            PriorityAlternative = PriorityAlternative.PassTurn });
        scope.Observe(priority);
        var passTurn = scope.Encode(new PassTurn(game, prompt));
        check((string)passTurn["action"]! == "priorityPassTurn" && (string)passTurn["expectedTask"]! == "priority",
            "The current pass-turn alternative has its own wire command and never falls through generic cancel");
        Reject(new UndoLastAction(game, prompt), "A current pass-turn alternative cannot be invoked as Undo");
        Reject(new SelectCard(game, prompt, source.Key), "A priority activation cannot be mistranslated as target selection");
        Reject(new PlayCard(game, prompt, source.Key), "A battlefield activation cannot be mistranslated as a hand-card play");
        var activation = scope.Encode(new ActivateCard(game, prompt, source.Key));
        check((string)activation["key"]! == source.Key && (string)activation["expectedTask"]! == "priority",
            "A current priority activation reaches the adapter with its original scoped source handle");

        int[] suppliedChoices = [3];
        var choices = State(new() { Kind = "choice", Min = 1, Max = 1, Choices = [new(3, "Offered", null)] });
        scope.Observe(choices);
        var choose = new ChooseOptions(game, prompt, suppliedChoices.ToImmutableArray());
        suppliedChoices[0] = 99;
        var choiceWire = scope.Encode(choose);
        check(((ImmutableArray<int>)choiceWire["choices"]!).SequenceEqual([3]) && (string)choiceWire["expectedTask"]! == "choice",
            "Choice encoding retains its immutable exact offered indices and semantic task");
        Reject(new OrderOptions(game, prompt, [3]), "An unordered chooser cannot be answered through an ordered-selection command");
        scope.Observe(State(new() { Kind = "text", Numeric = true, Min = 2, Max = 5 }));
        var numeric = scope.Encode(new ChooseNumber(game, prompt, 4));
        check((string)numeric["expectedTask"]! == "number" && (string)numeric["value"]! == "4",
            "A numeric text input is bound to the number task without a free-text fallback");
        Reject(new EnterText(game, prompt, "4"), "A numeric prompt cannot accept the free-text command even with numeric-looking text");

        var ignored = new PriorityAbility("ignored-ability", source.Key, source.Name, "Ability", true);
        scope.Observe(priority with { Decision = null, IgnoredPriorityAbilities = [ignored] });
        var restore = scope.Encode(new RestorePriorityAbility(game, ignored.Id));
        check(!restore.ContainsKey("expectedTask") && (string)restore["promptId"]! == "" && (bool)restore["ignored"]! == false,
            "Restoring an observed match preference remains a game-scoped setting without impersonating another player's prompt");
        scope.Observe(priority with { Decision = null, IgnoredPriorityAbilities = [] });
        Reject(new RestorePriorityAbility(game, ignored.Id), "Removing an observed preference revokes an already captured Restore command");

        scope.Observe(priority);
        scope.Clear();
        Reject(new PassPriority(game, prompt), "Clearing a process or match scope revokes its previously observed commands");
        scope.Observe(priority);
        scope.Observe(null);
        Reject(new PassPriority(game, prompt), "An authoritative null match observation revokes the preceding game's decision");
        bool replacementRejected = false;
        try { new ForgeCommandScope().Encode(new PassPriority(game, prompt)); }
        catch (EngineCommandRejectedException) { replacementRejected = true; }
        check(replacementRejected, "A replacement provider process starts with no command authority from the preceding process");
    }
}
