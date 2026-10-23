using System.Collections.Immutable;
using System.Text.Json;
using Mana.Contracts;
using Mana.Magic;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

internal sealed class CommanderTaxSmoke(TableWindow.Probe view, InteractionScenarioEngine scenario) : ITableAutomation
{
    private readonly List<string> checks = [];
    private int stage;
    private double started;
    private MouseState mouse;
    public string? ExpectedClick => null;
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;
    private void Move(int x, int y, ButtonState left = ButtonState.Released)
    {
        var viewport = view.GraphicsDevice.Viewport;
        float scale = Math.Min(viewport.Width / 1600f, viewport.Height / (float)view.DesignHeight);
        mouse = new((int)((viewport.Width - 1600 * scale) / 2 + x * scale), (int)((viewport.Height - view.DesignHeight * scale) / 2 + y * scale),
            0, left, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    }
    private void Buttons(ButtonState left = ButtonState.Released, ButtonState right = ButtonState.Released) =>
        mouse = new(mouse.X, mouse.Y, 0, left, ButtonState.Released, right, ButtonState.Released, ButtonState.Released);
    private void Check(bool value, string description)
    {
        if (!value) {
            File.WriteAllText(Path.Combine(view.Profile, "commander-tax-failure.json"), JsonSerializer.Serialize(new {
                description, stage, view.Now, view.Overlay, Match = view.Match, Frame = view.Frame?.Observe()
            }, new JsonSerializerOptions { WriteIndented = true }));
            throw new InvalidOperationException(description);
        }
        checks.Add(description);
    }
    private void Capture(Texture2D surface, string name) {
        using var file = File.Create(Path.Combine(view.Profile, name + ".png"));
        surface.SaveAsPng(file, surface.Width, surface.Height);
    }
    private void Publish(GameSnapshot state) { scenario.Present(state); view.Accept(scenario.State); }
    public void Frame(Texture2D surface)
    {
        if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
        if (view.Now > 25) throw new TimeoutException("Commander tax review at stage " + stage);
        if (view.ActivityOpen || view.Now - started < (stage == 0 ? 1.5 : .6)) return;
        if (stage == 3 && view.HoverPreview is not { Width: >= 400 } && view.Now - started < 3) return;
        switch (stage) {
            case 0:
                Check(view.Hits.Count(h => h.Zone == "Command" && h.Card != null) == 8, "Both commanders retain their card hit areas at all four seats");
                Check(view.Match!.Players.SelectMany(p => p.Zone("Command").Cards).All(c => !c.Selectable), "Tax remains visible even when commanders cannot be cast");
                Capture(surface, "01-partner-taxes");
                var center = view.DisplayedPose("commander-0")!.Value.Center;
                Move((int)center.X, (int)center.Y);
                break;
            case 1:
                Check(view.HoverPreview == null && scenario.Replies.Count == 0, "Hover highlights a commander without opening a reader or submitting a game action");
                Buttons(left: ButtonState.Pressed);
                break;
            case 2: Buttons(); break;
            case 3:
                Check(view.DisplayedPose("commander-0") is { Size.X: >= 400 } && view.HoverPreview is { Width: >= 400 }
                    && view.Match!.Viewer!.Zone("Command").Cards.Single(c => c.VisualId == "commander-0").CommanderTax == 2,
                    "An explicit click reads the taxed commander without a game action");
                Capture(surface, "02-tax-hover");
                Buttons(right: ButtonState.Pressed);
                break;
            case 4: Buttons(); break;
            case 5:
                var frame = view.Frame!;
                var reader = frame.Cards.SingleOrDefault(card => card.Id == "commander-0" && card.Focused);
                var text = frame.Observe().Text.Select(item => item.Value).ToArray();
                Check(view.Overlay.Length == 0 && reader != null && reader.Zone == "Command" && reader.Display.Size.X >= 400
                    && reader.Card is { ManaCost: "{3}{G}", CommanderTax: 2 } && view.CardPaintCount("commander-0") == 1
                    && text.Contains("{3}{G}") && text.Contains("COMMANDER TAX  +2")
                    && text.Any(value => value.Contains("Extra generic mana to cast from the command zone.", StringComparison.Ordinal))
                    && scenario.Replies.Count == 0,
                    "Right-click retains the canonical commander reader with the complete printed cost and separate current tax explanation without a command");
                Capture(surface, "03-tax-inspector");
                var close = SmokePointer.Hit(view, reader!.Hit);
                mouse = new(close.X, close.Y, 0, ButtonState.Pressed, ButtonState.Released,
                    ButtonState.Released, ButtonState.Released, ButtonState.Released);
                break;
            case 6: Buttons(); break;
            case 7:
                Check(view.Overlay.Length == 0 && view.HoverPreview == null
                    && !view.Frame!.Cards.Any(card => card.Id == "commander-0" && card.Focused)
                    && view.CardPaintCount("commander-0") == 1 && scenario.Replies.Count == 0,
                    "Clicking the commander again closes its canonical reader and retains its single command-zone face without a command");
                Move(0, 0);
                Publish(scenario.State with { Players = scenario.State.Players.Select(p => p with { Zones = p.Zones.Select(z => z with {
                    Cards = z.Cards.Select(c => c.VisualId == "commander-0" ? c with { CommanderTax = 6 } : c).ToImmutableArray()
                }).ToImmutableArray() }).ToImmutableArray() });
                break;
            case 8:
                Check(view.Match!.Viewer!.Zone("Command").Cards[0].CommanderTax == 6 && view.Match.Viewer.Zone("Command").Cards[1].CommanderTax == 4,
                    "A later engine snapshot updates only the commander whose tax changed");
                Capture(surface, "04-tax-updated");
                Publish(scenario.State with { Id = "new-commander-game", Players = scenario.State.Players.Select(p => p with { Zones = p.Zones.Select(z => z with {
                    Cards = z.Cards.Select(c => c with { CommanderTax = 0 }).ToImmutableArray()
                }).ToImmutableArray() }).ToImmutableArray() });
                break;
            case 9:
                Check(view.Match!.Players.SelectMany(p => p.Zone("Command").Cards).All(c => CardPresentation.CommanderTax(c) == 0), "A new game clears all previous commander taxes");
                Check(CardPresentation.CommanderTax(new() { FaceDown = true, CommanderTax = 8 }) == 0, "A face-down card cannot display commander identity through a tax badge");
                Check(scenario.Replies.Count == 0, "Tax badges and their inspection never submit a game decision");
                Capture(surface, "05-new-game");
                File.WriteAllText(Path.Combine(view.Profile, "commander-tax-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
                view.Exit();
                break;
        }
        started = view.Now; stage++;
    }

    // A checkerboard stand-in cannot prove that a printed mana cost is readable.
    // Missing commander artwork exercises the real complete-cost text fallback.
    internal sealed class CostReadableArtFixture : ICardArtSource
    {
        private readonly CardReadingArtFixture otherCards = new();
        public Task<byte[]?> LoadAsync(ArtRequest art, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return art.Name is "Goreclaw, Terror of Qal Sisma" or "Anara, Wolvid Familiar"
                ? Task.FromResult<byte[]?>(null) : otherCards.LoadAsync(art, cancellationToken);
        }
        public void Dispose() => otherCards.Dispose();
    }
}
