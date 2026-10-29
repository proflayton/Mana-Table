using System.Collections.Immutable;
using Mana.Client;
using Mana.Contracts;
using Microsoft.Xna.Framework;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    // Local selection describes a proposed arrangement; only fresh ordinary
    // engine commands can stage it. It is never another combat state replica.
    private string groupBlockDeclaration = "";
    private ImmutableHashSet<string> groupBlockSources = ImmutableHashSet<string>.Empty;
    private ImmutableHashSet<string> groupBlockTargets = ImmutableHashSet<string>.Empty;
    private CombatAssignmentIntent? groupBlockIntent;
    private GameSnapshot? groupBlockPreviewSnapshot;
    private ImmutableHashSet<string>? groupBlockPreviewSources, groupBlockPreviewTargets;
    private CombatAssignmentPreview? groupBlockPreview;
    private (string Game, int Viewer, int Turn, int? Active)? groupCombatScope;
    private string groupCombatDeclaration = "";
    private ImmutableHashSet<string> groupCombatAttackers = ImmutableHashSet<string>.Empty;
    private bool groupCombatStaged;
    private bool GroupBlocksOpen => groupBlockDeclaration.Length > 0;
    private bool GroupedCombatReview => groupCombatScope is { } scope && match is { Combat.Attackers.Length: > 0 } state
        && (scope.Game, scope.Viewer, scope.Turn) == (state.Id, state.ViewerId, state.Turn)
        && (scope.Active == state.ActivePlayerId || state.Status is "finished" or "error")
        && state.Decision?.Payload is not AttackPayload
        && (state.Status is "finished" or "error" || state.PhaseKey is
            "COMBAT_DECLARE_BLOCKERS" or "COMBAT_FIRST_STRIKE_DAMAGE" or "COMBAT_DAMAGE" or "COMBAT_END")
        && (state.Combat.DeclarationId.Length == 0 || state.Combat.DeclarationId == groupCombatDeclaration)
        && state.Combat.Attackers.All(attack => groupCombatAttackers.Contains(attack.CardId));
    private bool CanGroupBlock => match is { Status: "playing", Viewer.Eliminated: false, Decision.Payload: BlockPayload,
        Combat.DeclarationId.Length: > 0 } state && (state.DecisionOwnerId == null || state.DecisionOwnerId == state.ViewerId);
    private bool CanConfirmCombatNow => !GroupBlocksOpen && groupBlockIntent?.IsActive != true;

    private void BeginGroupBlocks()
    {
        if (!CanGroupBlock || busy || session.NeedsRecovery || match == null) return;
        StopQuietPriority(); StopStackResolution(); session.CancelAdvance(); CollapseCardReading();
        overlay = ""; selectedCombat = null;
        if (!GroupedCombatReview) {
            groupCombatScope = (match.Id, match.ViewerId, match.Turn, match.ActivePlayerId);
            groupCombatDeclaration = match.Combat!.DeclarationId;
            groupCombatAttackers = match.Combat.Attackers.Select(attack => attack.CardId).ToImmutableHashSet(StringComparer.Ordinal);
            groupCombatStaged = match.Combat.Attackers.Any(attack => !attack.BlockerIds.IsEmpty);
        }
        groupBlockDeclaration = match.Combat!.DeclarationId;
        groupBlockSources = ImmutableHashSet<string>.Empty; groupBlockTargets = ImmutableHashSet<string>.Empty;
    }
    private void StopGroupBlocks()
    {
        groupBlockIntent?.Stop(); groupBlockIntent = null;
        groupBlockDeclaration = "";
        groupBlockSources = ImmutableHashSet<string>.Empty; groupBlockTargets = ImmutableHashSet<string>.Empty;
        if (!groupCombatStaged) RetireGroupedCombatView();
        session.CancelAdvance();
    }
    private void RetireGroupedCombatView()
    {
        groupCombatScope = null; groupCombatDeclaration = ""; groupCombatAttackers = ImmutableHashSet<string>.Empty; groupCombatStaged = false;
    }
    private bool SelectGroupBlocks(Card card)
    {
        if (!GroupBlocksOpen || !CanGroupBlock || match == null) return false;
        if (groupBlockIntent != null) { Inspect(card); return true; }
        // A physical pile supplies occurrence membership, never a same-name
        // search. Its selected copies remain the same cards during camera moves.
        var id = CardViews.Identity(card);
        var members = BattlefieldFocused && battlefieldFocusPlan?.Tile(id) is { } tile ? tile.Group.MemberIds
            : tableScene?.Cohort(id)?.MemberIds ?? [id];
        var cards = members.Select(member => CardViews.FindZoneOccurrence(match, member))
            .Where(item => item is { Zone: "Battlefield" }).Select(item => item!.Value).ToArray();
        var own = cards.Where(item => item.PlayerId == match.ViewerId
                && match.Combat!.Attackers.Any(attack => attack.DefendingPlayerId == match.ViewerId && attack.EligibleBlockerIds.Contains(item.Card.CombatId)))
            .Select(item => item.Card.CombatId).ToImmutableHashSet(StringComparer.Ordinal);
        var incoming = cards.Select(item => item.Card.CombatId).Where(combatId => match.Combat!.Attackers
                .Any(attack => attack.CardId == combatId && attack.DefendingPlayerId == match.ViewerId)).ToImmutableHashSet(StringComparer.Ordinal);
        if (own.Count > 0) groupBlockSources = own.IsSubsetOf(groupBlockSources) ? groupBlockSources.Except(own) : groupBlockSources.Union(own);
        else if (incoming.Count > 0) groupBlockTargets = incoming.IsSubsetOf(groupBlockTargets) ? groupBlockTargets.Except(incoming) : groupBlockTargets.Union(incoming);
        else return false;
        return true;
    }
    private void SelectAllGroupBlocks(bool incoming)
    {
        if (!GroupBlocksOpen || !CanGroupBlock || match == null || busy) return;
        var attacks = match.Combat!.Attackers.Where(attack => attack.DefendingPlayerId == match.ViewerId).ToArray();
        if (incoming) groupBlockTargets = attacks.Select(attack => attack.CardId).ToImmutableHashSet(StringComparer.Ordinal);
        else groupBlockSources = match.Viewer!.Zone("Battlefield").Cards.Where(card => attacks.Any(attack => attack.EligibleBlockerIds.Contains(card.CombatId)))
            .Select(card => card.CombatId).ToImmutableHashSet(StringComparer.Ordinal);
    }
    private CombatAssignmentPreview? GroupBlockPreview
    {
        get {
            if (match == null || !GroupBlocksOpen) return null;
            if (!ReferenceEquals(match, groupBlockPreviewSnapshot) || !ReferenceEquals(groupBlockSources, groupBlockPreviewSources)
                    || !ReferenceEquals(groupBlockTargets, groupBlockPreviewTargets)) {
                groupBlockPreviewSnapshot = match; groupBlockPreviewSources = groupBlockSources; groupBlockPreviewTargets = groupBlockTargets;
                groupBlockPreview = CombatAssignmentPreview.PairOneEach(match, groupBlockSources, groupBlockTargets);
            }
            return groupBlockPreview;
        }
    }
    private bool GroupBlockSelected(Card card) => GroupBlocksOpen && (groupBlockSources.Contains(card.CombatId) || groupBlockTargets.Contains(card.CombatId));

    private void ApplyGroupBlocks(ImmutableArray<BlockAssignment> reviewed)
    {
        if (!CanGroupBlock || !GroupBlocksOpen || busy || session.NeedsRecovery || match == null) return;
        var fresh = GroupBlockPreview;
        if (fresh == null || fresh.Proposed.IsEmpty || !fresh.Desired.ToHashSet().SetEquals(reviewed)) {
            notice = "Combat changed. Review the highlighted groups again."; noticeUntil = now + 3; return;
        }
        groupBlockIntent = CombatAssignmentIntent.TryArm(match, fresh.Desired);
        if (groupBlockIntent != null) { groupCombatStaged = true; CollapseCardReading(); selectedCombat = null; error = ""; commandFailure = false; }
    }
    private bool UpdateGroupBlocks(bool interactionBlocked)
    {
        if (groupCombatScope != null && !GroupedCombatReview) RetireGroupedCombatView();
        if (!GroupBlocksOpen) return false;
        if (held || !CanGroupBlock || match == null || match.Combat!.DeclarationId != groupBlockDeclaration || session.NeedsRecovery || commandFailure) {
            StopGroupBlocks(); return false;
        }
        if (groupBlockIntent is not { } intent) return true;
        if (!intent.CanContinue(match)) {
            bool completed = intent.Completed;
            StopGroupBlocks();
            notice = completed ? "Blocks assigned. Review the battlefield, then confirm blocks." : "Group assignment stopped. Review the current blocks.";
            noticeUntil = now + 4; return false;
        }
        if (interactionBlocked) { intent.Pause(); session.CancelAdvance(); return true; }
        intent.Resume();
        if (!busy && !polling && intent.Advance(session, (_, _) => false)) pendingActionLabel = "Assigning reviewed blocks.";
        return true;
    }
    private bool DrawGroupBlockDock()
    {
        if (!GroupBlocksOpen) return false;
        int y = DecisionY(721);
        canvas.Text("Group blocks", 1266, y, Gold, .92f, groupBlockIntent == null ? 188 : 296, 1, true);
        if (groupBlockIntent is { } intent) {
            canvas.Text($"{intent.Total - intent.Remaining} / {intent.Total} blocks assigned", 1266, y + 35, Ink, .72f, 296, 2);
            Button("group-block-stop", "Take control", 1266, y + 108, 296, new MatchIntent.StopGroupBlocks(), true, true, 45);
            return true;
        }
        var preview = GroupBlockPreview;
        string status = preview == null ? "Choose your creatures, then incoming attackers."
            : $"{preview.Proposed.Length} blocks \u00b7 {preview.UnpairedAttackers} unpaired attackers";
        canvas.Text(status, 1266, y + 33, Ink, .67f, 296, 2);
        canvas.Text($"{groupBlockSources.Count} blockers \u2192 {groupBlockTargets.Count} attackers", 1266, y + 71, Muted, .62f, 296, 1);
        Button("group-block-all-own", "All blockers", 1266, y + 96, 144, new MatchIntent.SelectAllGroupBlocks(false), !busy, height: 29);
        Button("group-block-all-incoming", "All incoming", 1418, y + 96, 144, new MatchIntent.SelectAllGroupBlocks(true), !busy, height: 29);
        CompleteActionButton("group-block-assign", "Assign " + (preview?.Proposed.Length ?? 0) + " blocks", new(1266, y + 134, 296, 39),
            new MatchIntent.ApplyGroupBlocks(preview?.Desired ?? []), !busy && preview is { Proposed.Length: > 0 });
        int cancelWidth = canvas.TextWidth("Cancel", .76f) + 24;
        Button("group-block-cancel", "Cancel", 1562 - cancelWidth, y, cancelWidth, new MatchIntent.StopGroupBlocks(), !busy, height: 29);
        return true;
    }
}
