using Mana.Contracts;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace Mana.Table;

internal sealed partial class TableApplication
{
    private readonly DecisionTempoPresentation tempoPresentation = new();
    private bool TimedOut(int playerId) => match?.Tempo?.TimedOutPlayers.Contains(playerId) == true;
    private Task? tempoAcknowledgement;
    private double nextTempoAcknowledgement;

    private void DrawLobbyTempo(Lobby state)
    {
        if (state.Tempo is not { } timing) return;
        bool editable = !busy && state is { Mode: "hosting", MatchActive: false } && services.Tempo is { SupportsTempo: true };
        canvas.Text("Tempo", 94, 232, Muted, .72f, 100, 1, true);
        var presets = new[] { (TempoPreset.Off, "Off"), (TempoPreset.Relaxed, "120s"), (TempoPreset.Standard, "60s"), (TempoPreset.Fast, "30s") };
        for (int i = 0; i < presets.Length; i++) {
            var (preset, label) = presets[i];
            MenuAction("tempo:" + preset, label, new(200 + i * 126, 218, 115, 42),
                new ShellIntent.SetTempo(preset, timing.Expiry), editable, preset == timing.Preset, .77f);
        }
        if (timing.Enabled) {
            canvas.Text("On timeout", 755, 232, Muted, .72f, 170, 1, true);
            var outcomes = new[] { (TempoExpiry.Pass, "Pass + grace"), (TempoExpiry.Loss, "Lose seat"), (TempoExpiry.Warn, "Warn only") };
            for (int i = 0; i < outcomes.Length; i++) {
                var (expiry, label) = outcomes[i];
                MenuAction("tempo-expiry:" + expiry, label, new(930 + i * 201, 218, 190, 42),
                    new ShellIntent.SetTempo(timing.Preset, expiry), editable, expiry == timing.Expiry, .72f);
            }
        }
        string description = !timing.Enabled ? "No decision clock. The host can set a pace for this table."
            : timing.Expiry switch {
                TempoExpiry.Warn => $"{timing.Seconds}s per decision. The portrait clock warns; it never plays for you or removes your seat.",
                TempoExpiry.Loss => $"{timing.Seconds}s per decision. Running out of time loses your seat.",
                _ => $"{timing.Seconds}s per decision: pass priority on timeout; required choices get {timing.GraceSeconds}s grace, then lose the seat. Three unattended passes also lose the seat."
            };
        canvas.Text(description, 94, 278, timing.Enabled ? Gold : Muted, .70f, 1418, 2);
    }
    private void SetLobbyTempo(TempoPreset preset, TempoExpiry expiry)
    {
        if (busy || lobby is not { Mode: "hosting", MatchActive: false, Tempo: { } timing } state
            || services.Tempo is not { SupportsTempo: true } service || timing.Preset == preset && timing.Expiry == expiry) return;
        string tableId = state.TableId;
        Work(async () => {
            var updated = await service.ConfigureTempoAsync(tableId, preset, expiry);
            updates.Enqueue(() => { if (lobby?.TableId == tableId) session.AcceptLobby(updated); });
        }, "Updating table tempo", "Everyone will ready up with the new timing.", foreground: false);
    }
    private void UpdateTempo()
    {
        tempoPresentation.Observe(match, now);
        if (tempoAcknowledgement is { IsCompleted: false }) return;
        if (tempoAcknowledgement != null) {
            // A stale presentation acknowledgement is harmless. Connection faults
            // remain owned by the normal observation/recovery path.
            _ = tempoAcknowledgement.Exception;
            tempoAcknowledgement = null;
        }
        if (now < nextTempoAcknowledgement || busy || StartupVisible || ActivityOpen || session.NeedsRecovery
            || !matchPresentation.Ready(now) || match is not { Decision: not null, Tempo: { } frame } state
            || services.Tempo is not { SupportsTempo: true } service) return;
        var offered = frame.Clocks.FirstOrDefault(clock => clock.PlayerId == state.ViewerId && clock.Phase == TempoPhase.Preparing);
        if (offered == null) return;
        nextTempoAcknowledgement = now + 1;
        tempoAcknowledgement = service.PresentedAsync(state.Id, offered.Id);
    }
    private void DrawDecisionClock(Player player, Vector2 center, float radius)
    {
        var clock = tempoPresentation.For(player.Id, now);
        // Preparing/automatically passed windows must not flash a new ring.
        if (clock == null || clock.Phase == TempoPhase.Preparing || player.Eliminated) return;
        bool urgent = clock.Phase is TempoPhase.Grace or TempoPhase.Expired || clock.RemainingMs <= 15_000;
        var color = urgent ? Red : Gold * .65f;
        float fraction = clock.Fraction;
        float r = radius + 10;
        canvas.Ring(center, r, new Color(8, 16, 20) * .8f, 4);
        if (fraction > 0) canvas.Ring(center, r, color, urgent ? 4 : 2, -MathF.PI / 2, MathF.Tau * fraction);
        if (urgent) {
            var badge = new Rectangle((int)center.X - 23, (int)(center.Y + radius - 14), 46, 24);
            canvas.Rounded(badge, new Color(18, 24, 28), 7);
            canvas.CenterText(clock.Phase == TempoPhase.Expired ? "!" : Math.Ceiling(clock.RemainingMs / 1000d).ToString("0"), badge, Red, .7f, true);
        }
        float distance = Vector2.Distance(pointer.ToVector2(), center);
        if (distance >= radius + 6 && distance <= radius + 14)
            DrawTurnHint(clock.Phase switch {
                TempoPhase.Preparing => "Preparing your decision",
                TempoPhase.Grace => "Required choice · grace time",
                TempoPhase.Expired => "Decision time expired",
                _ => $"{Math.Ceiling(clock.RemainingMs / 1000d):0}s to decide"
            }, center);
    }
}
