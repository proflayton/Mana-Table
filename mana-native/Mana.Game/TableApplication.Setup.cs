using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private void ResetTableView()
    {
        StopQuietPriority();
        hits.RevokePublished(); Frame = null; tableScene = null; hovered = null;
        cardPositions.Clear(); heroPositions.Clear();
        decisionDraft.Clear(); zoneName = null; selectedCombat = null;
        combatPage = 0; combatCandidates = false; inspectedStack = null; reviewedAttacker = null;
        inspectedAction = null;
        handPage = zonePage = choicePage = 0; choiceFilter = "";
        automatic = !Automated && preferences.Automatic; pressed = null;
        ClearCardReading(); ClearReaderActions();
        pendingPreviewReference = null; pendingPreviewBounds = null; pendingPreviewCardId = null;
        seatPortraits.Clear(); overlay = ""; inspected = null; held = false; dragging = false; dragSource = null;
        collapsedDecision = false; textFocus = false; previousTurn = ""; notice = ""; noticeUntil = 0;
    }
    private void ReturnFromMatch()
    {
        if (IsWorkshopMatch) { ReturnToWorkshop(); return; }
        if (localMatch) { localMatch = false; session.Reset(); ResetTableView(); message = "Choose your next Commander game."; }
        else Work(async () => { await tables.ReturnToLobbyAsync(); var state = await tables.LobbyAsync(); updates.Enqueue(() => { lobby = state; session.Reset(); ResetTableView(); }); }, "Returning to your group", "Keeping the table together for the next game.", true);
    }
    private void ImportFile()
    {
        try {
            using var picker = new OpenFileDialog { Title = "Import a Commander deck", Filter = "Deck lists|*.txt;*.dck|All files|*.*" };
            if (picker.ShowDialog() == DialogResult.OK) ImportFilePath(picker.FileName);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException) {
            error = "Could not open the deck file picker. Try again. " + ex.Message;
        }
    }
    private void ImportFilePath(string path)
    {
        ImportDeck(async () => {
            string text;
            try { text = await File.ReadAllTextAsync(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                throw new IOException($"Could not read '{Path.GetFileName(path)}'. Check that it is available and you have permission, then choose the file again. Your current deck is unchanged.", ex);
            }
            return await catalog.ImportAsync(Path.GetFileNameWithoutExtension(path), text);
        }, true, "Importing deck file", "Opening the file, then reading its deck list.");
    }
    private void ImportClipboard()
    {
        string text;
        try {
            if (!Clipboard.ContainsText()) { error = "Copy an exported deck list to the clipboard first."; return; }
            text = Clipboard.GetText();
        } catch (System.Runtime.InteropServices.ExternalException) {
            error = "Could not read the clipboard. Another app may be using it. Try Paste again or import a deck file."; return;
        }
        if (WorkshopEngine != null && text.Trim().Contains("archidekt.com", StringComparison.OrdinalIgnoreCase) && !text.Trim().Contains('\n')) { ImportArchidekt(text.Trim()); return; }
        var name = Ask("Deck name", "Name your Commander deck", "My Commander deck");
        if (name != null) ImportDeck(() => catalog.ImportAsync(name, text), true);
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
