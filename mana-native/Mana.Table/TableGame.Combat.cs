using Mana.Magic;
using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private int combatPage;
    private bool combatCandidates;
    private string? inspectedStack;
    private string? reviewedAttacker;
    private readonly SceneEffects effects = new();
    private string? CombatSource => dragging ? dragSource?.Card?.CombatId : selectedCombat;
    private bool HasCombat => match?.Combat is { } combat && (combat.Attackers.Length > 0 || match.Decision?.Intent is DecisionIntent.Attack or DecisionIntent.Block);
    private bool CombatTarget(Card card) => match is { } state && CombatSource is { } source &&
        (LegalTargets.CanAttackCard(state, source, card.CombatId) || LegalTargets.CanBlock(state, source, card.CombatId));
    private void OpenCombat(bool candidates = false) { overlay = "combat"; combatCandidates = candidates; combatPage = 0; }
    private void OpenStack(int index = 0)
    {
        zoneName = "Stack"; zonePage = index; inspectPage = 0;
        inspectedStack = match?.Stack.ElementAtOrDefault(index)?.Id;
    }
    private void RevealCard(Card card)
    {
        if (match == null) return;
        var player = match.Players.FirstOrDefault(p => p.Zone("Battlefield").Cards.Any(c => c.CombatId == card.CombatId));
        if (player == null) return;
        bool land = card.Type.Contains("Land");
        var rank = player.Zone("Battlefield").Cards.Where(c => c.Type.Contains("Land") == land).ToArray();
        int index = Array.FindIndex(rank, c => c.CombatId == card.CombatId);
        pages[player.Id + (land ? ":lands" : ":permanents")] = Math.Max(0, index - 2);

    }
    private void RemoveAssignment(string source, string? attacker = null)
    {
        if (match != null && !busy && CombatGuide.Remove(match, source, attacker) is { } reply) Send(reply);
    }
    private void DrawCombatBar()
    {
        if (!HasCombat || match == null) return;
        var attacks = match.Combat!.Attackers;
        canvas.Gradient(new(1250, 623, 328, 88), Color.Transparent, new Color(14, 22, 27) * .8f);
        var selected = CombatSource == null ? null : CombatGuide.Find(match, CombatSource);
        string message = selected != null ? selected.Name + " · choose a blue target"
            : $"{attacks.Length} attacking · {attacks.Count(a => a.Blocked || a.BlockerIds.Length > 0)} blocked";
        if (!string.IsNullOrWhiteSpace(match.Combat.BlockProblem)) message = match.Combat.BlockProblem;
        canvas.Text(message, 1266, 628, selected != null ? Gold : Ink, .61f, 200, 2, true);
        if (selectedCombat != null) {
            Button("combat-clear", "Cancel", 1266, 677, 82, () => selectedCombat = null, height: 29);
            Button("combat-remove", "Remove", 1354, 677, 91, () => RemoveAssignment(selectedCombat!), !busy && CombatGuide.Remove(match, selectedCombat) != null, height: 29);
        }
        Button("combat-review", "Combat · C", 1451, 677, 111, () => OpenCombat(), height: 29);
    }
    private void DrawCombatReview()
    {
        var state = match!;
        ModalFrame(combatCandidates ? "Choose your creature" : "Combat assignments", () => overlay = "", "Back to table");
        bool assigning = state.Decision?.Intent is DecisionIntent.Attack or DecisionIntent.Block;
        Button("combat-assignments", "Assignments", 205, 181, 184, () => { combatCandidates = false; combatPage = 0; }, accent: !combatCandidates, height: 37);
        if (assigning) Button("combat-creatures", "Your creatures", 401, 181, 196, () => { combatCandidates = true; combatPage = 0; }, accent: combatCandidates, height: 37);
        canvas.Text(state.Combat?.BlockProblem ?? (selectedCombat == null ? "Inspect any combat, including creatures outside the current battlefield page." : "Choose an attacker below to assign the selected blocker."), 205, 231, Muted, .71f, 1170, 2);
        if (combatCandidates) {
            var candidates = state.Viewer?.Zone("Battlefield").Cards.Where(c => CombatGuide.CanSelect(state, c)).ToArray() ?? [];
            int count = 6; combatPage = Math.Clamp(combatPage, 0, Math.Max(0, (candidates.Length - 1) / count));
            int index = 0;
            foreach (var card in candidates.Skip(combatPage * count).Take(count)) {
                int x = 214 + index++ * 197; var pose = new Pose(new(x + 78, 413), new(156, 218));
                PaintCard(card, pose, selectedCombat == card.CombatId, true);
                canvas.Text(card.Name, x, 541, Ink, .74f, 176, 3, true);
                canvas.Text($"{card.Power}/{card.Toughness}", x, 610, Gold, .84f, 176, 1, true);
                if (pose.Contains(pointer)) hovered = card;
                hits.Add(new("combat-source:" + card.CombatId, Scope, new(x, 304, 176, 345), () => { selectedCombat = card.CombatId; overlay = ""; RevealCard(card); }, card));
            }
            if (candidates.Length == 0) canvas.Text("No creatures can be assigned for this decision.", 230, 362, Muted, 1);
            CombatPages(candidates.Length, count);
            return;
        }
        var attacks = state.Combat?.Attackers ?? [];
        combatPage = Math.Clamp(combatPage, 0, Math.Max(0, (attacks.Length - 1) / 3));
        int row = 0;
        foreach (var attack in attacks.Skip(combatPage * 3).Take(3)) {
            int y = 289 + row++ * 139; var card = CombatGuide.Find(state, attack.CardId);
            canvas.Panel(new(204, y, 1190, 127), new(27, 35, 45), Muted * .3f, 7);
            if (card != null) {
                var pose = new Pose(new(259, y + 62), new(67, 94)); PaintCard(card, pose, false, CombatTarget(card));
                if (pose.Contains(pointer)) hovered = card;
                hits.Add(new("combat-inspect:" + card.CombatId, Scope, pose.Bounds, () => Inspect(card), card));
            }
            canvas.Text(card?.Name ?? "Attacker", 309, y + 10, Ink, .84f, 373, 2, true);
            canvas.Text(card == null ? "" : $"{card.Power}/{card.Toughness}  " + string.Join(" · ", card.CombatKeywords), 309, y + 62, Gold, .66f, 375, 2);
            string defender = attack.Defender?.Name ?? state.Players.FirstOrDefault(p => p.Id == attack.DefendingPlayerId)?.Name ?? "Defender";
            canvas.Text("ATTACKING " + defender, 708, y + 12, Red, .66f, 443, 2, true);
            string blockers = string.Join(", ", attack.BlockerIds.Select(id => CombatGuide.Find(state, id)?.Name ?? "Blocker"));
            canvas.Text(blockers.Length > 0 ? "Blocked by " + blockers : attack.Blocked ? "Blocked · blockers have left combat" : "Unblocked", 708, y + 53, Ink, .68f, 446, 2);
            if (attack.BlockerIds.Length > 0) Button("combat-blockers:" + attack.CardId, "Review " + attack.BlockerIds.Length + " blockers", 708, y + 91, 274, () => { reviewedAttacker = attack.CardId; combatPage = 0; overlay = "blockers"; }, height: 27);
            if (selectedCombat != null && LegalTargets.CanBlock(state, selectedCombat, attack.CardId) && card != null) {
                bool assigned = attack.BlockerIds.Contains(selectedCombat);
                Button("combat-pair:" + attack.CardId, assigned ? "Remove block" : "Assign block", 1180, y + 20, 196, () => { overlay = ""; CardClick(card); }, !busy, true, 38);
            } else if (state.Decision?.Intent == DecisionIntent.Attack && CombatGuide.Remove(state, attack.CardId) != null) {
                Button("combat-recall:" + attack.CardId, "Recall attacker", 1180, y + 20, 196, () => RemoveAssignment(attack.CardId), !busy, height: 38);
            }
            if (card != null) Button("combat-locate:" + card.CombatId, "Show on table", 1180, y + 71, 196, () => { overlay = ""; RevealCard(card); }, height: 34);
        }
        if (attacks.Length == 0) canvas.Text("No attacks have been assigned yet.", 230, 362, Muted, 1);
        CombatPages(attacks.Length, 3);
    }
    private void CombatPages(int total, int count)
    {
        Button("combat-prev", "Previous", 207, 736, 141, () => combatPage--, combatPage > 0);
        Button("combat-next", "Next", 361, 736, 141, () => combatPage++, (combatPage + 1) * count < total);
        canvas.Text($"Page {combatPage + 1} / {Math.Max(1, (total + count - 1) / count)}", 529, 748, Muted, .69f, 430, 1);
        if (match != null && match.Decision?.Intent is DecisionIntent.Attack or DecisionIntent.Block)
            Button("combat-confirm", TurnGuide.Confirm(match), 1080, 730, 309, () => { overlay = ""; Send(new(match.Id, match.Decision!.Id, ReplyAction.Confirm)); }, !busy && CombatGuide.CanConfirm(match), true, 48);
    }
    private void DrawBlockerReview()
    {
        var state = match!;
        var attack = state.Combat?.Attackers.FirstOrDefault(a => a.CardId == reviewedAttacker);
        ModalFrame("Assigned blockers", () => overlay = "", "Back to table");
        if (attack == null) { canvas.Text("The attacker has left combat.", 226, 240, Muted, 1); return; }
        var attacker = CombatGuide.Find(state, attack.CardId);
        canvas.Text("Blocking " + (attacker?.Name ?? "this attacker"), 214, 186, Gold, .91f, 1100, 2, true);
        var blockers = attack.BlockerIds.Select(id => CombatGuide.Find(state, id)).Where(c => c != null).Cast<Card>().ToArray();
        int count = 6; combatPage = Math.Clamp(combatPage, 0, Math.Max(0, (blockers.Length - 1) / count));
        int index = 0;
        foreach (var card in blockers.Skip(combatPage * count).Take(count)) {
            int x = 214 + index++ * 197; var pose = new Pose(new(x + 78, 386), new(156, 218));
            PaintCard(card, pose, false, false);
            canvas.Text(card.Name, x, 515, Ink, .72f, 176, 3, true);
            canvas.Text($"{card.Power}/{card.Toughness}", x, 585, Gold, .84f, 176, 1, true);
            if (pose.Contains(pointer)) hovered = card;
            hits.Add(new("blocker-inspect:" + card.CombatId, Scope, pose.Bounds, () => Inspect(card), card));
            if (CombatGuide.Remove(state, card.CombatId, attack.CardId) != null) Button("blocker-remove:" + card.CombatId, "Remove block", x, 636, 173, () => RemoveAssignment(card.CombatId, attack.CardId), !busy, height: 35);
        }
        if (blockers.Length == 0) canvas.Text("No blockers remain assigned to this attacker.", 226, 327, Muted, 1);
        CombatPages(blockers.Length, count);
        Button("combat-overview", "Combat overview", 741, 736, 228, () => OpenCombat(), height: 40);
    }
    private void DrawCardEffects()
    {
        foreach (var cue in effects.Cards) {
            double age = now - cue.Started;
            if (age < 0 || age > 1.1 || !motion.TryGet(cue.VisualId, out var pose)) continue;
            float alpha = preferences.ReducedMotion ? 1 : (float)(1 - age / 1.1);
            var color = cue.Kind == CueKind.Damage ? Red : cue.Kind == CueKind.Arrive ? Gold : Blue;
            canvas.CardFrame(pose with { Size = pose.Size + new Vector2(8 + (preferences.ReducedMotion ? 0 : (float)age * 17)) }, color * alpha, 2);
            if (cue.Kind == CueKind.Arrive) {
                var badge = new Microsoft.Xna.Framework.Rectangle((int)pose.Center.X - 47, pose.Bounds.Top - 24, 94, 23);
                canvas.Panel(badge, new Color(30, 31, 22) * alpha, Gold * alpha, 4);
                canvas.CenterText("ENTERED", badge, Gold * alpha, .5f, true);
            }
            if (cue.Kind == CueKind.Damage) canvas.Text("−" + cue.Amount, (int)pose.Center.X + 25, (int)pose.Center.Y - 30 - (int)(age * 30), Red * alpha, 1.4f, 100, 1, true);
        }
    }
    private void DrawResult()
    {
        var state = match!;
        ModalFrame(state.Result ?? "Game finished", () => overlay = "", "Inspect table");
        canvas.CenterText(state.Result == "Victory" ? "VICTORY" : state.Result == "Defeat" ? "DEFEAT" : "GAME COMPLETE", new(300, 177, 1000, 89), state.Result == "Victory" ? Gold : Ink, 2.6f, true, true);
        canvas.CenterText($"Commander · {state.Players.Length} players · Turn {state.Turn}", new(300, 276, 1000, 37), Muted, .85f);
        for (int i = 0; i < state.Players.Length; i++) {
            var player = state.Players[i]; int y = 353 + i * 70;
            canvas.Text(player.Id == state.ViewerId ? "You" : player.Name, 263, y, Ink, 1, 702, 1, true);
            canvas.Text(player.Eliminated ? "Eliminated" : "Remaining", 973, y + 3, Muted, .8f, 195, 1);
            canvas.Text(player.Life + " life", 1183, y, Gold, .9f, 173, 1, true);
            canvas.Line(new(260, y + 47), new(1341, y + 47), Muted * .17f);
        }
        Button("result-history", "Match history", 260, 713, 253, () => { overlay = "history"; historyPage = 0; }, height: 52);
        Button("return", localMatch ? "Return to setup" : "Return to lobby", 991, 711, 349, ReturnFromMatch, !busy, true, 56);
    }
}
