using Mana.Client;
using Mana.Contracts;
using Mana.Renderer;
using Microsoft.Xna.Framework.Input;
using Point = Microsoft.Xna.Framework.Point;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;

namespace Mana.Table;

// Actual application input/composition with a fixture-owned update workflow.
// The controlled provider establishes lifecycle, not installer or rules behavior.
internal static class UpdateOwnershipChecks
{
    internal static void Run(string profile, Action<bool, string> check)
    {
        foreach (var size in new[] { new Point(1000, 740), new Point(1540, 980) })
            RunSize(Path.Combine(profile, "updates-owner-" + size.X), size, check).GetAwaiter().GetResult();
    }

    private static async Task RunSize(string profile, Point size, Action<bool, string> check)
    {
        Directory.CreateDirectory(profile);
        var source = new HeldUpdates();
        var owner = new AppUpdateSession(source, profile);
        PresentationRun? a = null, b = null;
        try {
            a = Open("a"); a.View.OpenAppUpdates(); Tick(a);
            Click(a, "updates-check"); await source.CheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.CheckGate.SetResult(source.Release); await Returned(); Tick(a); Tick(a);
            Click(a, "updates-download"); await source.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var oldCancel = a.Frame.Hits.Last(hit => hit.Id == "updates-cancel");
            var oldView = a.View;
            a.Dispose(); a = null;
            check(!oldView.CurrentHit(oldCancel), "Retiring the actual update view revokes its previously published pointer hit at " + size);
            b = Open("b"); b.View.OpenAppUpdates(); Tick(b);
            check(b.View.UpdateDownloading && source.Downloads == 1 && !source.Cancelled.Task.IsCompleted,
                "A new production application reads the original held download after the first Game is disposed at " + size);

            var cancel = b.Frame.Hits.Last(hit => hit.Id == "updates-cancel");
            Point physical = Physical(b, cancel.Bounds.Center);
            Tick(b, Mouse(physical, true));
            source.Progress!.Report(new(512, 1024)); await Returned(); Tick(b, Mouse(physical, true));
            check(b.View.UpdateBytes == 512 && b.View.CurrentHit(cancel),
                "Byte-only progress leaves the actual published Cancel hit current during a held pointer gesture at " + size);
            Tick(b, Mouse(physical, false));
            check(b.AcceptedHit == "updates-cancel", "The original Cancel press/release survives progress without reopening or relocating its control at " + size);
            await source.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.DownloadGate.SetResult(source.Prepared); await Returned(); Tick(b); Tick(b);
            check(!b.View.UpdateDownloading && b.View.PreparedUpdate == null && source.Downloads == 1,
                "The fresh view's cancellation settles the retained provider once at " + size);

            source.ResetDownload();
            var download = b.Frame.Hits.Last(hit => hit.Id == "updates-download");
            Click(b, "updates-download"); await source.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var beforePhase = b.Frame.Hits.Last(hit => hit.Id == "updates-cancel");
            source.Progress!.Report(new(1024, 1024) { Stage = UpdatePreparationStage.Preparing });
            await Returned(); Tick(b);
            check(!b.View.CurrentHit(beforePhase) && !b.View.CurrentHit(download),
                "A semantic phase transition revokes old preparation/download hits while byte progress alone does not at " + size);
            Tick(b); source.DownloadGate.SetResult(source.Prepared); await Returned(); Tick(b); Tick(b);
            var ready = b.View.PreparedUpdate;
            check(ready == source.Prepared && !b.View.CurrentHit(beforePhase),
                "The new prepared identity settles normally without reviving prior command hits at " + size);
            b.Dispose(); b = null;
            await owner.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
            check(source.Disposals == 0 && owner.View.Phase == AppUpdatePhase.Closed,
                "The actual Game can be gone before the retained owner closes without a Game pump or provider disposal at " + size);
            source.Dispose();
            check(source.Disposals == 1, "Composition retains the one provider disposal after update task settlement at " + size);
        } finally {
            a?.Dispose(); b?.Dispose(); source.Complete(); await owner.CloseAsync();
        }

        PresentationRun Open(string name)
        {
            string path = Path.Combine(profile, name); Directory.CreateDirectory(path);
            var scenario = new InteractionScenarioEngine(); scenario.Present(PresentationSmoke.Example());
            var run = new PresentationRun(TestTableServices.For(scenario), path, scenario.State, size,
                retainedUpdates: owner);
            Tick(run); return run;
        }
        async Task Returned()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await owner.WaitForQueuedWorkAsync(timeout.Token);
        }
        static MouseState Mouse(Point at, bool down) => new(at.X, at.Y, 0,
            down ? ButtonState.Pressed : ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
        static void Tick(PresentationRun run, MouseState mouse = default) => run.Tick(TimeSpan.FromMilliseconds(20), mouse);
        static Point Physical(PresentationRun run, Point logical)
        {
            var at = RenderViewport.Fit(run.Viewport, run.View.DesignHeight).ToPhysical(logical.ToVector2());
            return new((int)MathF.Round(at.X), (int)MathF.Round(at.Y));
        }
        void Click(PresentationRun run, string id)
        {
            var hit = run.Frame.Hits.Last(value => value.Id == id);
            var at = Physical(run, hit.Bounds.Center);
            Tick(run, Mouse(at, false)); Tick(run, Mouse(at, true)); Tick(run, Mouse(at, false));
            check(run.AcceptedHit == id, "The production pointer submits " + id + " through the retained view at " + size);
        }
    }

    private sealed class HeldUpdates : IAppUpdates, IDisposable
    {
        public AppBuild Build { get; } = new("0.2.9", true, new('a', 40));
        public AppRelease Release { get; } = new(new(0, 2, 10), new("https://example.invalid/release"), new("https://example.invalid/update"), 1024, new('a', 64));
        public PreparedUpdate Prepared => new(Release, "controlled-update.exe");
        internal TaskCompletionSource<AppRelease?> CheckGate = Gate<AppRelease?>();
        internal TaskCompletionSource<PreparedUpdate> DownloadGate = Gate<PreparedUpdate>();
        internal TaskCompletionSource CheckStarted = Gate(), DownloadStarted = Gate(), Cancelled = Gate();
        internal IProgress<UpdateDownloadProgress>? Progress;
        internal int Downloads, Disposals;
        public Task<AppRelease?> CheckAsync(CancellationToken cancellationToken = default)
        { CheckStarted.TrySetResult(); return CheckGate.Task; }
        public async Task<PreparedUpdate> DownloadAsync(AppRelease release, string cacheDirectory,
            IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Downloads++; Progress = progress;
            using var cancellation = cancellationToken.Register(() => Cancelled.TrySetResult());
            DownloadStarted.TrySetResult(); return await DownloadGate.Task;
        }
        public Task ValidateAsync(PreparedUpdate update, CancellationToken cancellationToken = default) => Task.CompletedTask;
        internal void ResetDownload() { DownloadGate = Gate<PreparedUpdate>(); DownloadStarted = Gate(); Cancelled = Gate(); }
        internal void Complete() { CheckGate.TrySetCanceled(); DownloadGate.TrySetCanceled(); }
        public void Dispose() => Disposals++;
        private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
