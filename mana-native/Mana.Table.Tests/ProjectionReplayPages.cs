using System.Collections.Immutable;
using Mana.Contracts;
using Mana.Scenarios;

namespace Mana.Table;

// Test-local continuous feed of independently bounded recording files. Storage
// seams never create another connection, application, baseline or display clock.
internal sealed record ProjectionReplayPages(int TotalObservations, ImmutableArray<long> AcceptedAtTicks,
    long? EndTicks, ProjectionPresentationSettings? Settings, Func<IEnumerable<ProjectionRecording>> Open)
{
    internal static ProjectionReplayPages Single(ProjectionRecording recording) => new(recording.Observations.Length,
        recording.Timeline?.AcceptedAtTicks ?? [], recording.Timeline?.EndTicks, recording.Timeline?.Settings, () => [recording]);
}

internal sealed class PagedProjectionPlayback : IMatchConnection, IDisposable
{
    private readonly ProjectionReplayPages source;
    private readonly IEnumerator<ProjectionRecording> pages;
    private ProjectionRecording page;
    private int pagePosition, position, rejected, requests;
    internal GameSnapshot Current => page.Observations[pagePosition];
    internal int Position => position;
    internal int RejectedCommands => rejected;
    internal int ObservationRequests => requests;
    internal PagedProjectionPlayback(ProjectionReplayPages source)
    {
        this.source = source; pages = source.Open().GetEnumerator();
        if (!pages.MoveNext()) throw new InvalidDataException("An observation stream requires its initial page.");
        page = pages.Current;
    }
    internal bool Advance()
    {
        if (position + 1 >= source.TotalObservations) return false;
        if (pagePosition + 1 >= page.Observations.Length) {
            var previous = Current;
            if (!pages.MoveNext()) throw new InvalidDataException("An observation stream omitted a storage page.");
            page = pages.Current; pagePosition = 0;
            var next = Current;
            if (next.Id != previous.Id || next.ViewerId != previous.ViewerId || next.Revision < previous.Revision
                || next.BoardRevision < previous.BoardRevision || next.Executions != null && previous.Executions != null
                    && next.Executions.LastSequence < previous.Executions.LastSequence)
                throw new InvalidDataException("A storage seam replaced or reversed the continuous owned audience.");
        } else pagePosition++;
        position++; return true;
    }
    public Task<GameSnapshot?> ObserveAsync() { requests++; return Task.FromResult<GameSnapshot?>(Current); }
    public Task ExecuteAsync(GameCommand command) => Reject();
    public Task ConcedeAsync(string gameId) => Reject();
    private Task Reject() { rejected++; return Task.FromException(new EngineCommandRejectedException("Recorded observations cannot execute or concede.")); }
    public void Dispose() => pages.Dispose();
}
