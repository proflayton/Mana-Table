using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mana.Contracts;

namespace Mana.Scenarios;

// A bounded, viewer-private window of observations, not saved rules state.
// Faces were admitted by the original provider. Retained handles are evidence
// only; playback has no connection to that provider and cannot execute them.
public sealed record ProjectionRecording
{
    public const int CurrentFormat = 1, MaximumObservations = 512;
    public const long MaximumBytes = 64L * 1024 * 1024;
    public int FormatVersion { get; }
    public string ProviderId { get; }
    public string? SourceRevision { get; }
    public ImmutableArray<GameSnapshot> Observations { get; }
    public ProjectionTimeline? Timeline { get; }

    [JsonConstructor]
    public ProjectionRecording(int formatVersion, string providerId, string? sourceRevision,
        ImmutableArray<GameSnapshot> observations, ProjectionTimeline? timeline = null)
    {
        if (formatVersion != CurrentFormat || string.IsNullOrWhiteSpace(providerId) || providerId.Length > 128
            || providerId.Any(char.IsControl) || sourceRevision != null && (sourceRevision.Length != 40
                || sourceRevision.Any(value => value is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            || observations.IsDefaultOrEmpty || observations.Length > MaximumObservations)
            throw Invalid();
        GameSnapshot? previous = null;
        foreach (var state in observations) {
            if (state == null || string.IsNullOrWhiteSpace(state.Id) || state.Viewer == null
                || state.Revision < 0 || state.BoardRevision < 0
                || previous != null && (state.Id != previous.Id || state.ViewerId != previous.ViewerId
                    || state.Revision < previous.Revision || state.BoardRevision < previous.BoardRevision
                    || state.Executions != null && previous.Executions != null
                        && state.Executions.LastSequence < previous.Executions.LastSequence)) throw Invalid();
            ExecutionFrames.Validate(state);
            PublicActivityFacts.Validate(state);
            previous = state;
        }
        if (timeline != null && timeline.AcceptedAtTicks.Length != observations.Length) throw Invalid();
        FormatVersion = formatVersion; ProviderId = providerId; SourceRevision = sourceRevision; Observations = observations;
        Timeline = timeline;
    }

    public void WriteNew(string path)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var bounded = new LimitedOutput(file);
        JsonSerializer.Serialize(bounded, this);
    }

    public static ProjectionRecording Read(string path, string? expectedProvider = null, string? expectedSource = null)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length <= 0 || file.Length > MaximumBytes) throw Invalid();
        var recording = JsonSerializer.Deserialize<ProjectionRecording>(file) ?? throw Invalid();
        if (expectedProvider != null && recording.ProviderId != expectedProvider
            || expectedSource != null && recording.SourceRevision != expectedSource) throw Invalid();
        return recording;
    }

    private static ArgumentException Invalid() => new("A projection recording requires one ordered viewer scope, supported schema and explicit provider/source provenance.");

    private sealed class LimitedOutput(Stream output) : Stream
    {
        private long written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => output.CanWrite;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > MaximumBytes - written) throw new IOException("The private projection window exceeds its recording byte limit.");
            output.Write(buffer); written += buffer.Length;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

// Production presentation can observe this port, but a recording never provides
// setup, source-action discovery, a rules engine or executable input authority.
public sealed class ProjectionPlayback(ProjectionRecording recording) : IMatchConnection
{
    private readonly object gate = new();
    private readonly ProjectionRecording recording = recording ?? throw new ArgumentNullException(nameof(recording));
    private int position, rejectedCommands, observationRequests;
    public GameSnapshot Current { get { lock (gate) return recording.Observations[position]; } }
    public int Position { get { lock (gate) return position; } }
    public int RejectedCommands { get { lock (gate) return rejectedCommands; } }
    public int ObservationRequests { get { lock (gate) return observationRequests; } }
    public bool Advance()
    {
        lock (gate) {
            if (position + 1 >= recording.Observations.Length) return false;
            position++; return true;
        }
    }
    public Task<GameSnapshot?> ObserveAsync()
    {
        lock (gate) { observationRequests++; return Task.FromResult<GameSnapshot?>(recording.Observations[position]); }
    }
    public Task ExecuteAsync(GameCommand command) => Reject();
    public Task ConcedeAsync(string gameId) => Reject();
    private Task Reject()
    {
        lock (gate) rejectedCommands++;
        return Task.FromException(new EngineCommandRejectedException("Recorded observations are read-only; their historical handles cannot answer or concede a game."));
    }
}
