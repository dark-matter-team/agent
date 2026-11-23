using System.Collections.Concurrent;
using System.Threading.Channels;
using Roadmap.Agent.Models;
using Roadmap.Agent.Pipeline;

namespace Roadmap.Api.Jobs;

public enum JobStatus { Queued, Running, Done, Failed }

/// <summary>
/// What the client sees: generation status and (when ready) the roadmap.
/// The client polls GET /api/roadmaps/{id} every 2–3 seconds.
/// </summary>
public sealed record RoadmapJob
{
    public required Guid Id { get; init; }
    public required JobStatus Status { get; init; }
    public string? Progress { get; init; }
    public LearningRoadmap? Roadmap { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? Error { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// MVP: jobs and the queue live in memory and are lost on restart.
/// Next step — PostgreSQL (roadmaps as JSONB) + Hangfire for the queue.
/// </summary>
public sealed class RoadmapJobStore(TimeProvider time)
{
    private readonly ConcurrentDictionary<Guid, Entry> _jobs = new();

    private readonly Channel<Guid> _queue = Channel.CreateBounded<Guid>(new BoundedChannelOptions(100)
    {
        FullMode = BoundedChannelFullMode.Wait,
    });

    private sealed record Entry(UserProfile Profile, RoadmapJob Job);

    public ChannelReader<Guid> Queue => _queue.Reader;

    /// <summary>null — the queue is full.</summary>
    public RoadmapJob? Enqueue(UserProfile profile)
    {
        var now = time.GetUtcNow();
        var job = new RoadmapJob
        {
            Id = Guid.NewGuid(),
            Status = JobStatus.Queued,
            Progress = "Queued…",
            CreatedAt = now,
            UpdatedAt = now,
        };

        _jobs[job.Id] = new Entry(profile, job);
        if (_queue.Writer.TryWrite(job.Id))
        {
            return job;
        }

        _jobs.TryRemove(job.Id, out _);
        return null;
    }

    public RoadmapJob? Get(Guid id) => _jobs.TryGetValue(id, out var e) ? e.Job : null;

    public UserProfile GetProfile(Guid id) => _jobs[id].Profile;

    public void Update(Guid id, Func<RoadmapJob, RoadmapJob> change) =>
        _jobs.AddOrUpdate(
            id,
            _ => throw new KeyNotFoundException(id.ToString()),
            (_, e) => e with { Job = change(e.Job) with { UpdatedAt = time.GetUtcNow() } });
}

/// <summary>
/// Background worker: takes jobs from the queue and runs the agent pipeline.
/// Generation takes 1–2 minutes, so the HTTP request does not wait for it.
/// </summary>
public sealed class RoadmapWorker(
    RoadmapJobStore store,
    RoadmapGenerator generator,
    IConfiguration configuration,
    ILogger<RoadmapWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var parallel = Math.Max(1, configuration.GetValue("Jobs:MaxParallel", 2));
        return Task.WhenAll(Enumerable.Range(0, parallel).Select(_ => ConsumeAsync(stoppingToken)));
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await foreach (var id in store.Queue.ReadAllAsync(ct))
        {
            store.Update(id, j => j with { Status = JobStatus.Running, Progress = "Starting…" });
            var progress = new CallbackProgress(message => store.Update(id, j => j with { Progress = message }));

            try
            {
                var result = await generator.GenerateAsync(store.GetProfile(id), progress, ct);
                store.Update(id, j => j with
                {
                    Status = JobStatus.Done,
                    Progress = null,
                    Roadmap = result.Roadmap,
                    Warnings = result.Warnings,
                });
            }
            catch (RoadmapGenerationException e)
            {
                logger.LogWarning(e, "Generation {Id} failed", id);
                store.Update(id, j => j with { Status = JobStatus.Failed, Progress = null, Error = e.Message });
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // Details go to the log only. Internal API errors are never shown to the client.
                logger.LogError(e, "Generation {Id} crashed", id);
                store.Update(id, j => j with
                {
                    Status = JobStatus.Failed,
                    Progress = null,
                    Error = "Could not build the roadmap. Please try again later.",
                });
            }
        }
    }

    private sealed class CallbackProgress(Action<string> onReport) : IProgress<string>
    {
        public void Report(string value) => onReport(value);
    }
}
