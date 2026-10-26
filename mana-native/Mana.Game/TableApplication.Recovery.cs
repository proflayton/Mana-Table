using Mana.Contracts;
using Microsoft.Xna.Framework;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private bool DrawRecoveryDock(GameSnapshot state)
    {
        if (!session.NeedsRecovery) return false;
        canvas.Text(polling ? "RECONNECTING" : "CONNECTION INTERRUPTED", 1266, DecisionY(724), Red, .69f, 298, 1, true);
        var age = session.LastSynchronizedAt is { } at ? Math.Max(0, (int)(clock.GetUtcNow() - at).TotalSeconds) : 0;
        canvas.Text($"Last update {age}s ago. " + (session.UncertainCommand
            ? "Checking your last action; please wait."
            : "Reconnecting before play resumes."), 1266, DecisionY(750), Ink, .75f, 295, 3);
        Button("recovery-retry", polling ? "Checking connection…" : "Reconnect", 1266, DecisionY(828), 177,
            new MatchIntent.RetryConnection(), !busy && !polling, true, 42);
        Button("recovery-leave", localMatch ? "Exit game" : "Leave", 1450, DecisionY(828), 113,
            new MatchIntent.OpenView(MatchView.LeaveTable), !busy, height: 42);
        return true;
    }

    private void DrawLeaveTable()
    {
        bool host = lobby?.Mode == "hosting";
        ModalFrame(localMatch ? "Exit this game?" : host ? "Close your hosted table?" : "Leave this table?",
            new MatchIntent.CloseView(), "Stay");
        string explanation = localMatch ? "This local game will end. Your saved deck stays available."
            : host ? "You are hosting. Leaving closes the table connection for everyone and ends this hosted game."
            : match?.Viewer?.Eliminated == true ? "You have been eliminated. The remaining players can keep playing when you leave."
            : "Leaving concedes your seat if you are still playing. The remaining players can continue.";
        canvas.Text(explanation, 232, 247, Ink, 1.14f, 1120, 5);
        if (session.NeedsRecovery) canvas.Text("The table is out of date. Leaving disconnects this client; an undelivered departure may not reach the host.", 232, 415, Gold, .92f, 1120, 4);
        Button("confirm-leave-table", host ? "Close hosted table" : "Leave table", 1000, 690, 340, new MatchIntent.LeaveTable(), !busy, true, 56);
    }

    private void LeaveMatchTable()
    {
        if (localMatch) {
            overlay = "";
            if (IsWorkshopMatch || match?.Status is "finished" or "error") { ReturnFromMatch(); return; }
            string id = match!.Id;
            Work(async () => {
                await matchConnection.ConcedeAsync(id);
                updates.Enqueue(ReturnFromMatch);
            }, "Ending your game", "Conceding this local game before returning to setup.", true);
            return;
        }
        overlay = "";
        Work(async () => {
            await tables.LeaveAsync();
            updates.Enqueue(() => {
                lobby = null; session.Reset(); ResetTableView(); InvalidateDeckConfirmation();
                setupModal = ""; message = "You left the table. Choose your next game.";
            });
        }, "Leaving the table", "Closing your connection and returning to setup.", true);
    }
}
