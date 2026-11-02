using System.Collections.Immutable;
using Mana.Contracts;
using Microsoft.Xna.Framework;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private int cooperativePartySeats = 1, cooperativeOppositionSeats = 1;
    private string setupRuleset = MatchRulesets.CooperativeCommander;
    private static bool IsCooperative(Lobby state) => state.MatchSetup?.RulesetId == MatchRulesets.CooperativeCommander;
    private bool CanConfigureMatch => services.MatchSetup is { SupportsMatchSetup: true }
        && lobby is { Mode: "hosting", MatchActive: false, MatchSetupCapabilities: not null };

    private static MatchSetupSpecification CooperativeSpecification(int party, int opposition, Lobby? current = null)
    {
        int count = party + opposition;
        var roles = new MatchSeatRole?[count];
        bool cooperative = current != null && IsCooperative(current);
        int partyTeam = cooperative ? current!.MatchSetup!.Seats.First(seat => seat.Role == MatchSeatRole.Party).TeamId : 0;
        int enemyTeam = cooperative ? current!.MatchSetup!.Seats.First(seat => seat.Role == MatchSeatRole.Opposition).TeamId : 1;
        // Growing a party must not silently recruit the enemy that occupied the
        // next index. Preserve enrolled sides; only empty/new places fill gaps.
        foreach (var seat in current?.Seats ?? []) {
            if (seat.Index >= count || seat.Type == "OPEN") continue;
            roles[seat.Index] = cooperative ? seat.Role
                : seat.Type is "LOCAL" or "REMOTE" ? MatchSeatRole.Party : MatchSeatRole.Opposition;
        }
        int friends = roles.Count(role => role == MatchSeatRole.Party), enemies = roles.Count(role => role == MatchSeatRole.Opposition);
        if (friends > party) throw new InvalidOperationException($"This size keeps {friends} occupied party places but allows {party}. Choose a size that retains your party.");
        if (enemies > opposition) throw new InvalidOperationException($"This size keeps {enemies} occupied opposition places but allows {opposition}. Choose a size that retains the opposing side.");
        foreach (var seat in current?.Seats ?? []) {
            if (seat.Index >= count || roles[seat.Index] != null) continue;
            if (seat.Role == MatchSeatRole.Party && friends < party) { roles[seat.Index] = MatchSeatRole.Party; friends++; }
            else if (seat.Role == MatchSeatRole.Opposition && enemies < opposition) { roles[seat.Index] = MatchSeatRole.Opposition; enemies++; }
        }
        for (int index = 0; index < count; index++) {
            if (roles[index] != null) continue;
            if (friends < party) { roles[index] = MatchSeatRole.Party; friends++; }
            else { roles[index] = MatchSeatRole.Opposition; enemies++; }
        }
        return new(MatchRulesets.CooperativeCommander, Enumerable.Range(0, count).Select(index =>
            new MatchSeatAssignment(index, roles[index] == MatchSeatRole.Party ? partyTeam : enemyTeam, roles[index]!.Value)).ToImmutableArray());
    }

    private MatchSetupSpecification ProposedSpecification(Lobby state) => setupRuleset == MatchRulesets.CooperativeCommander
        ? CooperativeSpecification(cooperativePartySeats, cooperativeOppositionSeats, state)
        : new(MatchRulesets.FreeForAllCommander, state.Seats.Select(seat =>
            new MatchSeatAssignment(seat.Index, seat.Index, MatchSeatRole.Participant)).ToImmutableArray());

    private string? ProposedSetupProblem
    {
        get {
            if (!CanConfigureMatch || lobby is not { } state) return "The host can change these rules before the game starts.";
            int count = setupRuleset == MatchRulesets.CooperativeCommander
                ? cooperativePartySeats + cooperativeOppositionSeats : state.Seats.Length;
            if (state.Seats.Any(seat => (seat.Type is "LOCAL" or "REMOTE") && seat.Index >= count))
                return "That size would remove a connected friend. Choose a size that keeps their place.";
            MatchSetupSpecification specification;
            try { specification = ProposedSpecification(state); }
            catch (InvalidOperationException problem) { return problem.Message; }
            try {
                MatchSetupFacts.Validate(specification, state.MatchSetupCapabilities);
            } catch (InvalidOperationException) { return "Choose one of this host's supported party and opposition sizes."; }
            return state.Seats.Where(seat => seat.Type is "LOCAL" or "REMOTE").Any(seat =>
                !specification.Seats.Any(assignment => assignment.Index == seat.Index && assignment.Role != MatchSeatRole.Opposition))
                    ? "That setup would move a connected friend onto the opposition. Keep them in your party." : null;
        }
    }
    private bool CanApplyMatchSetup => ProposedSetupProblem == null;

    private void DrawCooperativeWelcome(int top)
    {
        canvas.Text("Your party against the table's AI.", 94, top + 73, Ink, 1.02f, 620, 2, true);
        canvas.Text("Your side", 94, top + 120, Muted, .75f, 140, 1);
        for (int count = 1; count <= 4; count++)
            MenuAction("cooperative-party-size:" + count, count.ToString(), new(252 + (count - 1) * 70, top + 108, 54, 42),
                new ShellIntent.SetMatchPartySeats(count), !busy, cooperativePartySeats == count, .8f);
        canvas.Text("Choose 1 for solo, or leave places for friends and AI allies. Each player keeps their own turns, life, cards and mana.",
            94, top + 162, Muted, .70f, 620, 3);
        Button("host-cooperative", "Host co-op encounter", 90, top + 220, 430, new ShellIntent.Connect(true),
            loaded && !busy && CommanderDeck && DeckReady, true, 64, prominent: true);
        MenuAction("join", "Join with an invite", new(90, top + 291, 430, 36), new ShellIntent.OpenJoin(), loaded && !busy);
        if (!CommanderDeck) canvas.Text("Choose a Commander deck for this encounter.", 94, top + 328, Gold, .72f, 620, 2);
    }

    private void OpenMatchSetup()
    {
        if (busy || !CanConfigureMatch || lobby is not { } state) return;
        setupRuleset = state.MatchSetup?.RulesetId ?? MatchRulesets.FreeForAllCommander;
        if (IsCooperative(state)) {
            cooperativePartySeats = state.MatchSetup!.Seats.Count(seat => seat.Role == MatchSeatRole.Party);
            cooperativeOppositionSeats = state.MatchSetup.Seats.Count(seat => seat.Role == MatchSeatRole.Opposition);
        }
        setupModal = "match-setup"; textFocus = false;
    }

    private void ChooseMatchRuleset(string ruleset)
    {
        if (busy || setupModal != "match-setup" || !CanConfigureMatch
            || ruleset is not (MatchRulesets.FreeForAllCommander or MatchRulesets.CooperativeCommander)
            || lobby!.MatchSetupCapabilities?.RulesetIds.Contains(ruleset) != true) return;
        setupRuleset = ruleset;
        if (ruleset == MatchRulesets.CooperativeCommander) {
            int required = lobby.Seats.Where(seat => seat.Type is "LOCAL" or "REMOTE").Select(seat => seat.Index + 1).DefaultIfEmpty(1).Max();
            cooperativePartySeats = Math.Clamp(Math.Max(cooperativePartySeats, required), 1, 4);
        }
    }

    private void SetMatchPartySeats(int count)
    {
        if (!busy && !Connected && cooperativeTab && setupModal.Length == 0 && match == null && count is >= 1 and <= 4) {
            cooperativePartySeats = count; return;
        }
        if (busy || setupModal != "match-setup" || !CanConfigureMatch
            || count < 1 || count > lobby!.MatchSetupCapabilities!.MaximumPartySeats) return;
        cooperativePartySeats = count;
    }

    private void SetMatchOppositionSeats(int count)
    {
        if (busy || setupModal != "match-setup" || !CanConfigureMatch
            || count < 1 || count > lobby!.MatchSetupCapabilities!.MaximumOppositionSeats) return;
        cooperativeOppositionSeats = count;
    }

    private void ApplyMatchSetup()
    {
        if (busy || !CanApplyMatchSetup || lobby is not { } current || services.MatchSetup is not { } service) return;
        var specification = ProposedSpecification(current);
        Work(async () => {
            var receipt = await service.ConfigureMatchAsync(current.TableId, current.Revision, specification);
            updates.Enqueue(() => {
                if (lobby?.TableId != current.TableId || !session.AcceptLobby(receipt)) return;
                setupModal = ""; homePlayMode = IsCooperative(receipt) ? HomePlayMode.Cooperative : HomePlayMode.Friends;
                InvalidateDeckConfirmation();
                message = "Table rules updated. Everyone should review their side and Ready again.";
            });
        }, "Preparing the encounter", "Sharing teams and rules with everyone at the table.");
    }

    private void DrawMatchSetup()
    {
        var capabilities = lobby?.MatchSetupCapabilities;
        if (capabilities == null) return;
        canvas.Text("Choose your table's rules", 212, 192, Ink, 1.45f, 1000, 2, true);
        var choices = new[] { (MatchRulesets.FreeForAllCommander, "Free-for-all Commander"),
            (MatchRulesets.CooperativeCommander, "Cooperative Commander") };
        for (int index = 0; index < choices.Length; index++) {
            var (ruleset, label) = choices[index];
            Button("match-ruleset:" + ruleset, label, 216 + index * 580, 282, 552,
                new ShellIntent.ChooseMatchRuleset(ruleset), !busy && capabilities.RulesetIds.Contains(ruleset), setupRuleset == ruleset, 62);
        }
        if (setupRuleset == MatchRulesets.CooperativeCommander) {
            canvas.Text("Your party", 216, 399, Gold, 1.05f, 420, 1, true);
            canvas.Text("Friends and optional AI allies", 216, 441, Muted, .76f, 470, 2);
            for (int count = 1; count <= capabilities.MaximumPartySeats; count++)
                MenuAction("match-party-size:" + count, count.ToString(), new(800 + (count - 1) * 119, 401, 94, 55),
                    new ShellIntent.SetMatchPartySeats(count), !busy, cooperativePartySeats == count, 1);
            canvas.Text("Opposition", 216, 520, Blue, 1.05f, 420, 1, true);
            canvas.Text("AI enemies; choose their decks at the table", 216, 562, Muted, .76f, 510, 2);
            for (int count = 1; count <= capabilities.MaximumOppositionSeats; count++)
                MenuAction("match-opposition-size:" + count, count.ToString(), new(800 + (count - 1) * 119, 522, 94, 55),
                    new ShellIntent.SetMatchOppositionSeats(count), !busy, cooperativeOppositionSeats == count, 1);
            canvas.Text("Separate turns, life totals, cards and mana. Your side wins together when the opposing side is defeated.",
                216, 659, Muted, .8f, 1130, 3);
        } else canvas.Text("Each player plays for themselves, with their own turns, life total, cards and mana.",
            216, 410, Muted, .9f, 1100, 3);
        if (ProposedSetupProblem is { } problem) canvas.Text(problem,
            216, 746, Gold, .72f, 1100, 2);
        Button("match-setup-apply", "Use these rules", 920, 804, 426, new ShellIntent.ApplyMatchSetup(), !busy && CanApplyMatchSetup,
            true, 54, prominent: true);
    }
}
