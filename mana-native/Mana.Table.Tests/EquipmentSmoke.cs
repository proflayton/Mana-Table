using System.Text.Json;
using Mana.Contracts;
using Mana.Renderer;
using Mana.Scenarios;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace Mana.Table;

// Production attachment edges, readers and camera input. Rules and controller
// projection are established separately; these checks do not approve clarity.
internal sealed class EquipmentSmoke : ITableAutomation
{
    private readonly TableWindow.Probe view;
    private readonly InteractionScenarioEngine scenario;
    private readonly Queue<(double Delay, Action Action)> steps = [];
    private readonly List<string> checks = [];
    private MouseState mouse;
    private KeyboardState keyboard;
    private double lastStep;
    public string? ExpectedClick => null;
    public bool ClickAccepted { get; set; }
    public MouseState ReadMouse() => mouse;
    public KeyboardState? ReadKeyboard() => keyboard;

    internal static void Run(string profile, Point? size)
    {
        new PlayPreferences { Automatic = false, Sounds = false, ReducedMotion = true, TextScale = 1.3f }.Save(profile);
        var scenario = new InteractionScenarioEngine(); scenario.Present(AttachmentScenario.Create()[0].Snapshot);
        using var game = new TableWindow(TestTableServices.For(scenario), profile, null, null, size, initialState: scenario.State,
            artSource: new CardReadingArtFixture(), enableLiveTableStudy: true);
        game.Automation = new EquipmentSmoke(game.TestPort, scenario); game.Run();
    }

    private EquipmentSmoke(TableWindow.Probe view, InteractionScenarioEngine scenario)
    {
        this.view = view; this.scenario = scenario;
        var stages = AttachmentScenario.Create();
        Step(.5, () => {
            Check(view.TextScale == 1.3f, "The actual production attachment journey uses enlarged text");
            CheckAssembly(AttachmentScenario.Boots, AttachmentScenario.Host);
            CheckAssembly(AttachmentScenario.Aura, AttachmentScenario.Host);
            CheckAssembly(AttachmentScenario.Jitte, AttachmentScenario.Host);
            Check(Node(AttachmentScenario.Foreign).Owner == 0 && Node(AttachmentScenario.Foreign).Attachment!.RootOwner == 1,
                "A cross-controller Aura sits beneath its actual host while retaining its own controller");
            Check(view.BattlefieldLabels(AttachmentScenario.Jitte).Any(label => label.Kind == "counter" && label.Text == "2"),
                "Jitte retains its own two counters on its exposed edge");
            Capture("01-attachments-under-host"); MoveEdge(AttachmentScenario.Boots);
        }); Click();
        Step(.25, () => { CheckReading(AttachmentScenario.Boots); Capture("02-read-equipment-edge"); MoveEdge(AttachmentScenario.Jitte); }); Click();
        Step(.25, () => {
            CheckReading(AttachmentScenario.Jitte);
            Check(view.Frame!.Drawing.Commands.OfType<DrawCommand.Text>().Any(text => text.Value == "2 Charge counters"),
                "The exact attached permanent's reader exposes its typed current counter total");
            Escape();
        }); ReleaseKeyboard();
        Step(.2, () => MoveCard(AttachmentScenario.Host)); Click();
        Step(.25, () => {
            CheckReading(AttachmentScenario.Host);
            var reader = view.Frame!.Cards.Single(card => card.Id == AttachmentScenario.Host);
            Check(reader.Card is { Power: 7, Toughness: 4 } && view.Frame.Drawing.Commands.OfType<DrawCommand.CenterText>()
                    .Any(text => text.Value == "7/4" && reader.Display.Bounds.Contains(text.Bounds)),
                "The host reader paints authoritative 7/4 without adding attachment bonuses again");
            Capture("03-host-current-state"); Escape();
        }); ReleaseKeyboard();
        Step(.2, () => MoveEdge(AttachmentScenario.Foreign)); Click();
        Step(.25, () => { CheckReading(AttachmentScenario.Foreign); Escape(); }); ReleaseKeyboard();
        Step(.2, () => Publish(stages[1].Snapshot));
        Step(.3, () => {
            Check(Node(AttachmentScenario.Boots).Attachment is { } moved && moved.HostId != AttachmentScenario.Host,
                "Re-equipping moves the same Boots occurrence to the new actual host");
            Capture("04-reequipped"); Publish(stages[3].Snapshot);
        });
        Step(.3, () => {
            Check(Node(AttachmentScenario.Host).Card.Tapped && !Node(AttachmentScenario.Boots).Card.Tapped,
                "A tapped host does not invent a tapped state for its attached Equipment");
            Capture("05-tapped-host"); Publish(stages[2].Snapshot);
        });
        Step(.3, () => {
            Check(Node(AttachmentScenario.Boots).Attachment == null,
                "Detached Equipment returns to its controller's independent battlefield objects");
            Capture("06-detached"); Publish(stages[4].Snapshot);
        });
        Step(1.25, () => {
            Check(view.Frame!.Scene!.BattlefieldDrawOrder.Count == 480,
                "The crowded attachment case retains all actual occurrences; this is not a readability assertion");
            CheckAssembly(AttachmentScenario.Foreign, Node(AttachmentScenario.Foreign).Card.AttachedTo!.Id);
            Capture("07-crowded-attachments"); Move(view.Frame.Hits.Last(hit => hit.Id == "focus:1").Bounds.Center);
        }); Click();
        Step(.25, () => {
            MoveEdge(AttachmentScenario.Foreign);
            mouse = new(mouse.X, mouse.Y, mouse.ScrollWheelValue + 480, default, default, default, default, default);
        });
        Step(.25, () => MoveEdge(AttachmentScenario.Foreign)); Click();
        Step(.25, () => {
            CheckReading(AttachmentScenario.Foreign);
            Check(scenario.Replies.Count == 0, "The entire attachment inspection/camera journey sends no game command");
            Capture("08-read-crowded-attachment");
            File.WriteAllText(Path.Combine(view.Profile, "equipment-results.json"), JsonSerializer.Serialize(checks));
            view.Exit();
        });
    }

    private SceneCard Node(string id) => view.Frame!.Scene!.BattlefieldDrawOrder.Single(node => node.Id == id);
    private void CheckAssembly(string id, string host)
    {
        var nodes = view.Frame!.Scene!.BattlefieldDrawOrder.ToArray();
        var child = Node(id); var target = nodes.Single(node => node.Id == host);
        Check(child.Attachment?.HostId == host && Array.IndexOf(nodes, child) < Array.IndexOf(nodes, target),
            "The exact attachment paints beneath its current host: " + id);
    }
    private void CheckReading(string id)
    {
        Check(view.Frame!.Cards.Single(card => card.Id == id) is { Focused: true, Display.Size.X: >= 300 }
                && scenario.Replies.Count == 0 && view.AttachmentConnections.Count == 0,
            "The actual card edge opens its exact full reader without a relationship line or game action: " + id);
    }
    private void MoveEdge(string id)
    {
        var node = Node(id); MoveChecked(id, node.Attachment!.Edge(view.BoardView.Apply(node.Pose)).Center);
    }
    private void MoveCard(string id) => MoveChecked(id, view.BoardView.Apply(Node(id).Pose).Center.ToPoint());
    private void MoveChecked(string id, Point point)
    {
        Check(view.HitAt(point)?.Card?.VisualId == id, "The card's actual exposed center owns its pointer target: " + id);
        Move(point);
    }
    private void Move(Point logical)
    {
        var viewport = view.GraphicsDevice.Viewport;
        var point = RenderViewport.Fit(new(viewport.Width, viewport.Height), view.DesignHeight).ToPhysical(logical.ToVector2()).ToPoint();
        mouse = new(point.X, point.Y, mouse.ScrollWheelValue, default, default, default, default, default);
    }
    private void Click()
    {
        Step(.03, () => mouse = new(mouse.X, mouse.Y, mouse.ScrollWheelValue, ButtonState.Pressed, default, default, default, default));
        Step(.04, () => mouse = new(mouse.X, mouse.Y, mouse.ScrollWheelValue, default, default, default, default, default));
    }
    private void Escape() => keyboard = new(Keys.Escape);
    private void ReleaseKeyboard() => Step(.04, () => keyboard = new());
    private void Publish(GameSnapshot state) { scenario.Present(state); view.Accept(scenario.State); }
    private void Step(double delay, Action action) => steps.Enqueue((delay, action));
    private void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); checks.Add(text); }
    private void Capture(string name)
    {
        using var output = File.Create(Path.Combine(view.Profile, name + ".png"));
        view.Surface.SaveAsPng(output, view.Surface.Width, view.Surface.Height);
    }
    public void Frame(Texture2D surface)
    {
        if (view.Now > 40) throw new TimeoutException("Production attachment journey did not finish.");
        if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
        if (!view.Loaded || view.StartupVisible) return;
        try {
            if (steps.TryPeek(out var step) && view.Now - lastStep >= step.Delay) { steps.Dequeue(); step.Action(); lastStep = view.Now; }
        } catch { Capture("failure"); throw; }
    }
}
