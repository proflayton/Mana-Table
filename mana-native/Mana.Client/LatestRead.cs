namespace Mana.Client;

public enum ReadStatus { Empty, Pending, Loading, Ready, Failed, Closed }

public sealed record ReadView<TContext, TValue>(TContext? Context, long Generation, ReadStatus Status,
    TValue? Value, string Error) where TContext : class where TValue : class;

/// <summary>
/// A session-thread owner for one replaceable read. At most one provider call is
/// active; intermediate requests coalesce into the latest intent. Completions
/// carry their original generation, including failures and after Clear/Close.
/// The caller controls when pending work starts (for example, after typing).
/// </summary>
public sealed class LatestRead<TContext, TValue>(Action<Action> post)
    where TContext : class where TValue : class
{
    private readonly CancellationTokenSource lifetime = new();
    private Func<Task<TValue>>? pending;
    private bool inFlight;
    public ReadView<TContext, TValue> View { get; private set; } = new(null, 0, ReadStatus.Empty, null, "");
    public bool Busy => View.Status is ReadStatus.Pending or ReadStatus.Loading;

    public void Request(TContext context, Func<Task<TValue>> read)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(read);
        if (View.Status == ReadStatus.Closed) return;
        pending = read;
        View = new(context, View.Generation + 1, ReadStatus.Pending, null, "");
    }

    public void Start()
    {
        if (inFlight || View.Status != ReadStatus.Pending || pending is not { } read) return;
        var request = View;
        pending = null; inFlight = true;
        View = request with { Status = ReadStatus.Loading };
        var cancellation = lifetime.Token;
        _ = Task.Run(async () => {
            TValue? value = null;
            Exception? failure = null;
            try { value = await read().WaitAsync(cancellation); }
            catch (Exception ex) { failure = ex; }
            post(() => {
                inFlight = false;
                if (View.Status == ReadStatus.Closed || View.Generation != request.Generation) return;
                View = request with { Status = failure == null && value != null ? ReadStatus.Ready : ReadStatus.Failed,
                    Value = failure == null ? value : null, Error = failure?.Message ?? (value == null ? "No result was returned." : "") };
            });
        });
    }

    public void Clear()
    {
        if (View.Status is ReadStatus.Empty or ReadStatus.Closed) return;
        pending = null;
        View = new(null, View.Generation + 1, ReadStatus.Empty, null, "");
    }

    public void Close()
    {
        if (View.Status == ReadStatus.Closed) return;
        pending = null;
        View = new(null, View.Generation + 1, ReadStatus.Closed, null, "");
        lifetime.Cancel();
    }
}
