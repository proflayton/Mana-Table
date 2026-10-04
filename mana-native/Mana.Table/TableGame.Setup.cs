using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableGame
{
    private void ResetTableView()
    {
        pages.Clear(); selections.Clear(); zoneName = null; selectedCombat = null;
        effects.Clear(); presence.Clear(); playback.Reset(); combatPage = 0; combatCandidates = false; inspectedStack = null; reviewedAttacker = null;
        actionFeedback.Clear(); departures.Clear(); inspectedAction = null;
        handPage = zonePage = choicePage = 0; choiceFilter = decisionId = "";
        automatic = Automation == null && preferences.Automatic; priority.Reset(); pressed = null; pendingPlay = null;
        ClearCardReading();
        motion.Clear(); seatPortraits.Clear(); overlay = ""; inspected = null; held = false; dragging = false; dragSource = null;
        collapsedDecision = false; textFocus = false; feedback.Clear(); previousTurn = ""; notice = ""; noticeUntil = 0;
    }
    private void ReturnFromMatch()
    {
        if (localMatch) { localMatch = false; session.Reset(); ResetTableView(); message = "Choose your next Commander game."; }
        else Work(async () => { await engine.ReturnToLobbyAsync(); var state = await engine.LobbyAsync(); updates.Enqueue(() => { lobby = state; session.Reset(); ResetTableView(); }); });
    }
    private void ImportFile()
    {
        using var picker = new OpenFileDialog { Title = "Import a Commander deck", Filter = "Deck lists|*.txt;*.dck|All files|*.*" };
        if (picker.ShowDialog() != DialogResult.OK) return;
        string name = Path.GetFileNameWithoutExtension(picker.FileName), text = File.ReadAllText(picker.FileName);
        ImportDeck(() => engine.ImportAsync(name, text), true);
    }
    private void ImportClipboard()
    {
        if (!Clipboard.ContainsText()) { error = "Copy an exported deck list to the clipboard first."; return; }
        var text = Clipboard.GetText(); var name = Ask("Deck name", "Name your Commander deck", "My Commander deck");
        if (name != null) ImportDeck(() => engine.ImportAsync(name, text), true);
    }
    private static string? Ask(string title, string instruction, string initial)
    {
        using var form = new Form { Text = title, Width = 600, Height = 220, StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var label = new Label { Text = instruction, Left = 16, Top = 18, Width = 550, Height = 38 };
        var input = new TextBox { Text = initial, Left = 16, Top = 62, Width = 550 };
        var ok = new System.Windows.Forms.Button { Text = "Confirm", Left = 352, Top = 110, Width = 100, DialogResult = DialogResult.OK };
        var cancel = new System.Windows.Forms.Button { Text = "Cancel", Left = 466, Top = 110, Width = 100, DialogResult = DialogResult.Cancel };
        form.Controls.AddRange([label, input, ok, cancel]); form.AcceptButton = ok; form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK ? input.Text : null;
    }
}
