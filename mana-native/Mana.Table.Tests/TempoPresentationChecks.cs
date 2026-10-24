using System.Text.Json;
using Mana.Contracts;
using Mana.Table.Presentation;

namespace Mana.Table;

internal static class TempoPresentationChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var settings = new TempoSettings(TempoPreset.Standard, TempoExpiry.Pass, 60, 60, "policy-one");
        var preparing = new DecisionClock(0, "choice-one", TempoPhase.Preparing, 60_000, 60_000, 0);
        var state = PresentationSmoke.Example() with { Tempo = new(settings, [preparing]) };
        var paint = new DecisionTempoPresentation();
        paint.Observe(state, 10);
        check(paint.For(0, 50)?.RemainingMs == 60_000 && paint.For(1, 50) == null,
            "Presentation never consumes the host's preparation allowance or invents another seat's timer");
        state = state with { Tempo = new(settings, [preparing with { Phase = TempoPhase.Thinking, RemainingMs = 12_000 }]) };
        paint.Observe(state, 50);
        check(paint.For(0, 55)?.RemainingMs == 7_000 && paint.For(0, 80)?.RemainingMs == 0,
            "Portrait countdown interpolates a received clock and clamps to zero without submitting a rules action");
        paint.Observe(state, 60);
        check(paint.For(0, 60)?.RemainingMs == 2_000, "Reading and repainting the same snapshot cannot refund decision time");
        paint.Observe(state with { Tempo = null }, 61);
        check(paint.For(0, 61) == null, "A processing or terminal snapshot removes the old timer");
        var wire = """{"settings":{"preset":"fast","expiry":"pass","seconds":30,"graceSeconds":60,"revision":"p"},"clocks":[{"playerId":2,"id":"c","phase":"grace","remainingMs":45000,"limitMs":60000,"strikes":1}]}""";
        var frame = JsonSerializer.Deserialize<DecisionTempoFrame>(wire, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        check(frame is { Settings.Preset: TempoPreset.Fast, Settings.Expiry: TempoExpiry.Pass }
            && frame.Clocks[0] is { Phase: TempoPhase.Grace, RemainingMs: 45_000 },
            "The provider wire maps to closed neutral tempo states, including off-turn required-choice grace");
    }
}
