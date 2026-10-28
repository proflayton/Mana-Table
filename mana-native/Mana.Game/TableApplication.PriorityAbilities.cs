using Mana.Contracts;
using Mana.Magic;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private string priorityAbilityId = "";
    private int priorityAbilityTextPage;

    private PriorityAbility[] PriorityAbilityOptions()
    {
        if (match is not { } state || state.Status is not ("playing" or "resolving") || state.Decision is { Payload: not PriorityPayload }) return [];
        return ((state.Decision?.Payload as PriorityPayload)?.Abilities ?? []).Concat(state.IgnoredPriorityAbilities)
            .Where(a => !string.IsNullOrWhiteSpace(a.Id)).DistinctBy(a => a.Id).ToArray();
    }

    private void SetAbilityAutoPass(PriorityAbility ability, bool ignored)
    {
        StopQuietPriority();
        if (match is not { } state || state.Status is not ("playing" or "resolving")) return;
        if (ignored) {
            if (state.Decision is not { Payload: PriorityPayload priority } prompt
                || !priority.Abilities.Any(a => a.Id == ability.Id)) return;
            Send(new IgnorePriorityAbility(state.Id, prompt.Id, ability.Id));
        } else {
            if (!state.IgnoredPriorityAbilities.Any(a => a.Id == ability.Id)) return;
            error = "";
            session.SubmitSetting(new(state.Id, ability.Id));
        }
        overlay = "";
    }

    private void PriorityAbilityButton(string id, string label, Rectangle bounds, UiIntent action, bool enabled = true, bool active = false)
    {
        PaintControlSurface(id, bounds, active ? new(29, 62, 70) : new(28, 39, 50),
            active ? Blue : new Color(73, 92, 106), enabled);
        canvas.CenterText(label, new(bounds.X + 5, bounds.Y, bounds.Width - 10, bounds.Height), enabled ? Ink : Muted, .57f);
        if (enabled) hits.Add(new(id, ScopeFor(action), bounds, action));
    }

    private void DrawPriorityAbilityControl(PriorityAbility[] abilities)
    {
        if (QuietPriorityArmed) return;
        if (decisionDock?.Surface == DecisionDockSurface.Portrait && decisionPanelBounds.IsEmpty) return;
        var bounds = new Rectangle(1266, DecisionY(852), 296, 23);
        if (tableScene?.PlayAccess.Groups.Count > 0 && (decisionDock?.Surface == DecisionDockSurface.Portrait || QuietPriorityArmed))
            bounds.Y = Math.Min(bounds.Y, decisionPanelBounds.Top - bounds.Height - 12);
        if (abilities.Length == 1) {
            var ability = abilities[0];
            PriorityAbilityButton(ability.IgnoredUntilNextTurn ? "ability-undo:" + ability.Id : "ability-pass:" + ability.Id,
                ability.IgnoredUntilNextTurn ? "Until my next turn  ·  Restore" : automatic ? "Pass ability until my next turn" : "Let Auto pass until my next turn", bounds,
                new MatchIntent.SetPriorityAbility(ability.Id, !ability.IgnoredUntilNextTurn), ability.IgnoredUntilNextTurn || !busy, ability.IgnoredUntilNextTurn);
        } else {
            int ignored = abilities.Count(a => a.IgnoredUntilNextTurn);
            PriorityAbilityButton("ability-auto-pass-open", ignored == 0 ? "Pass for an ability…" : $"{ignored} passing until my next turn  ·  Edit", bounds,
                new MatchIntent.OpenPriorityPassing(), active: ignored > 0);
        }
    }

    private void DrawAbilityAutoPass()
    {
        var abilities = PriorityAbilityOptions();
        if (abilities.Length == 0) return;
        int index = Math.Max(0, Array.FindIndex(abilities, a => a.Id == priorityAbilityId));
        var ability = abilities[index];
        ModalFrame("Pass for an ability", new MatchIntent.CloseView());
        canvas.Text("During a response, Auto can ignore this activation until your next turn. Other available responses still stop it.", 204, 188, Muted, .8f, 1165, 2);
        canvas.Text(ability.SourceName, 204, 253, Gold, 1.1f, 1165, 2, true);
        var text = matchReadingPages;
        int page = Math.Clamp(priorityAbilityTextPage, 0, text.Length - 1);
        canvas.Text(text[page], 204, 322, Ink, .92f, 1165, 10);
        if (text.Length > 1) {
            Button("ability-text-prev", "Previous text", 204, 605, 185, new MatchIntent.SetPage(MatchPage.PriorityText, page - 1), page > 0, height: 35);
            Button("ability-text-next", "More text", 402, 605, 185, new MatchIntent.SetPage(MatchPage.PriorityText, page + 1), page + 1 < text.Length, height: 35);
        }
        canvas.Text(ability.IgnoredUntilNextTurn ? "Passing this ability until your next turn. You can still activate it manually."
            : "You can still activate this ability after choosing to pass for it.", 204, 658, ability.IgnoredUntilNextTurn ? Blue : Muted, .76f, 1165, 2);
        if (!automatic) canvas.Text("Full control is on. This preference takes effect when you turn Auto on.", 204, 697, Muted, .7f, 1165, 1);
        if (abilities.Length > 1) {
            Button("ability-prev", "Previous ability", 204, 738, 210, new MatchIntent.ReadPriorityAbility(abilities.ElementAtOrDefault(index - 1)?.Id ?? ""), index > 0);
            Button("ability-next", "Next ability", 427, 738, 185, new MatchIntent.ReadPriorityAbility(abilities.ElementAtOrDefault(index + 1)?.Id ?? ""), index + 1 < abilities.Length);
            canvas.Text($"{index + 1} / {abilities.Length}", 637, 751, Muted, .7f);
        }
        PriorityAbilityButton(ability.IgnoredUntilNextTurn ? "ability-undo:" + ability.Id : "ability-pass:" + ability.Id,
            ability.IgnoredUntilNextTurn ? "Stop for this ability" : "Pass this ability until my next turn", new(939, 733, 436, 48),
            new MatchIntent.SetPriorityAbility(ability.Id, !ability.IgnoredUntilNextTurn), ability.IgnoredUntilNextTurn || !busy, ability.IgnoredUntilNextTurn);
    }
}
