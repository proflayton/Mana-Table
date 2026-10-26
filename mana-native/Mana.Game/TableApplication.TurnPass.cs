using Mana.Contracts;
using Microsoft.Xna.Framework;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    // Local confirmation retains one exact command, never permission to pass
    // a later prompt or another turn. Observations do not renew this intent.
    private sealed record TurnPassRequest(DecisionCommand Command, int Viewer, int Turn, string Phase);
    private TurnPassRequest? turnPassRequest;

    private static bool NeedsFirstLandConfirmation(GameSnapshot state, DecisionCommand command) =>
        state.Status == "playing" && state.Turn > 0 && state.ActivePlayerId == state.ViewerId
        && state.Viewer is { LandPlaysUsed: 0, Eliminated: false }
        && (command is PassTurn || command is PassPriority && state.PhaseKey == "MAIN2" && state.Stack.IsEmpty)
        && GameCommandPermissions.Allows(state, command);

    private void ReconcileTurnPass()
    {
        if (turnPassRequest is not { } request) return;
        if (session.NeedsRecovery || match is not { } state || state.ViewerId != request.Viewer
            || state.Turn != request.Turn || state.PhaseKey != request.Phase
            || !NeedsFirstLandConfirmation(state, request.Command)) turnPassRequest = null;
    }

    private void ConfirmTurnPass()
    {
        ReconcileTurnPass();
        if (turnPassRequest is not { } request) return;
        turnPassRequest = null;
        Send(request.Command, confirmedTurnPass: true);
    }

    private bool DrawTurnPassConfirmation(Rectangle area)
    {
        if (turnPassRequest == null) return false;
        canvas.Text("No land played this turn", area.X, area.Y - 2, Gold, .62f, area.Width, 1, true);
        int width = (area.Width - 8) / 2;
        var action = new Rectangle(area.X, area.Bottom - 40, width, 40);
        Button("turn-pass-keep", "Keep playing", action, new MatchIntent.DismissTurnPass(), !busy, true);
        Button("turn-pass-confirm", "Pass anyway", new(action.Right + 8, action.Y, width, action.Height),
            new MatchIntent.ConfirmTurnPass(), !busy);
        return true;
    }
}
