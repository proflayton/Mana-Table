using Mana.Contracts;
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
        if (match is not { } state || state.Status is not ("playing" or "resolving") || state.Decision is { Intent: not DecisionIntent.Priority }) return [];
        return (state.Decision?.PriorityAbilities ?? []).Concat(state.IgnoredPriorityAbilities)
            .Where(a => !string.IsNullOrWhiteSpace(a.Id)).DistinctBy(a => a.Id).ToArray();
    }

    private void SetAbilityAutoPass(PriorityAbility ability, bool ignored)
    {
        if (match is not { } state || state.Status is not ("playing" or "resolving")) return;
        if (ignored) {
            if (state.Decision is not { Intent: DecisionIntent.Priority } prompt
                || !prompt.PriorityAbilities.Any(a => a.Id == ability.Id)) return;
            Send(new(state.Id, prompt.Id, ReplyAction.SetAbilityAutoPass, AbilityId: ability.Id, Ignore: true));
        } else {
            if (!state.IgnoredPriorityAbilities.Any(a => a.Id == ability.Id)) return;
            error = "";
            session.SubmitSetting(new(state.Id, "", ReplyAction.SetAbilityAutoPass, AbilityId: ability.Id, Ignore: false));
        }
        overlay = "";
    }

    private void PriorityAbilityButton(string id, string label, Rectangle bounds, Action action, bool enabled = true, bool active = false)
    {
        bool over = bounds.Contains(pointer);
        canvas.Panel(bounds, active ? new Color(29, 62, 70) : over ? new Color(49, 62, 75) : new Color(28, 39, 50),
            active ? Blue : new Color(73, 92, 106), 4);
        canvas.CenterText(label, new(bounds.X + 5, bounds.Y, bounds.Width - 10, bounds.Height), enabled ? Ink : Muted, .57f);
        if (enabled) hits.Add(new(id, ControlScope(id), bounds, action));
    }

    private void DrawPriorityAbilityControl(PriorityAbility[] abilities)
    {
        var bounds = new Rectangle(1266, 772, 296, 23);
        if (abilities.Length == 1) {
            var ability = abilities[0];
            PriorityAbilityButton(ability.IgnoredUntilNextTurn ? "ability-undo:" + ability.Id : "ability-pass:" + ability.Id,
                ability.IgnoredUntilNextTurn ? "Until my next turn  ·  Undo" : automatic ? "Pass ability until my next turn" : "Let Auto pass until my next turn", bounds,
                () => SetAbilityAutoPass(ability, !ability.IgnoredUntilNextTurn), ability.IgnoredUntilNextTurn || !busy, ability.IgnoredUntilNextTurn);
        } else {
            int ignored = abilities.Count(a => a.IgnoredUntilNextTurn);
            PriorityAbilityButton("ability-auto-pass-open", ignored == 0 ? "Pass for an ability…" : $"{ignored} passing until my next turn  ·  Edit", bounds,
                () => { overlay = "ability-auto-pass"; priorityAbilityId = abilities[0].Id; priorityAbilityTextPage = 0; }, active: ignored > 0);
        }
    }

    private void DrawAbilityAutoPass()
    {
        var abilities = PriorityAbilityOptions();
        if (abilities.Length == 0) { overlay = ""; return; }
        int index = Math.Max(0, Array.FindIndex(abilities, a => a.Id == priorityAbilityId));
        var ability = abilities[index]; priorityAbilityId = ability.Id;
        ModalFrame("Pass for an ability", () => overlay = "");
        canvas.Text("Auto can pass this ability until your next turn. Other available plays still stop it.", 204, 188, Muted, .8f, 1165, 2);
        canvas.Text(ability.SourceName, 204, 253, Gold, 1.1f, 1165, 2, true);
        var text = TextPages(ability.Text, 850);
        priorityAbilityTextPage = Math.Clamp(priorityAbilityTextPage, 0, text.Length - 1);
        canvas.Text(text[priorityAbilityTextPage], 204, 322, Ink, .92f, 1165, 10);
        if (text.Length > 1) {
            Button("ability-text-prev", "Previous text", 204, 605, 185, () => priorityAbilityTextPage--, priorityAbilityTextPage > 0, height: 35);
            Button("ability-text-next", "More text", 402, 605, 185, () => priorityAbilityTextPage++, priorityAbilityTextPage + 1 < text.Length, height: 35);
        }
        canvas.Text(ability.IgnoredUntilNextTurn ? "Passing this ability until your next turn. You can still activate it manually."
            : "You can still activate this ability after choosing to pass for it.", 204, 658, ability.IgnoredUntilNextTurn ? Blue : Muted, .76f, 1165, 2);
        if (!automatic) canvas.Text("Full control is on. This preference takes effect when you turn Auto on.", 204, 697, Muted, .7f, 1165, 1);
        if (abilities.Length > 1) {
            Button("ability-prev", "Previous ability", 204, 738, 210, () => { priorityAbilityId = abilities[index - 1].Id; priorityAbilityTextPage = 0; }, index > 0);
            Button("ability-next", "Next ability", 427, 738, 185, () => { priorityAbilityId = abilities[index + 1].Id; priorityAbilityTextPage = 0; }, index + 1 < abilities.Length);
            canvas.Text($"{index + 1} / {abilities.Length}", 637, 751, Muted, .7f);
        }
        PriorityAbilityButton(ability.IgnoredUntilNextTurn ? "ability-undo:" + ability.Id : "ability-pass:" + ability.Id,
            ability.IgnoredUntilNextTurn ? "Undo · stop for this ability" : "Pass this ability until my next turn", new(939, 733, 436, 48),
            () => SetAbilityAutoPass(ability, !ability.IgnoredUntilNextTurn), ability.IgnoredUntilNextTurn || !busy, ability.IgnoredUntilNextTurn);
    }
}
