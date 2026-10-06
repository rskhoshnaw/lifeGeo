using System.Collections.Concurrent;

namespace LifeDrop.API.Observability;

public sealed record JobStatus(string Name, long Runs, long Failures, DateTimeOffset? LastSuccess, DateTimeOffset? LastRun, bool LastRunFailed, TimeSpan Interval, bool Stale);

// What the background jobs (alert scan, push, scheduled reports) report after every run (plan step 3.7). A job is "stale" when its last success is older than
// three intervals, or when it has only ever failed; readiness shows it as degraded and the metrics let an external alert fire.
public sealed class JobHealth
{
    public const int StaleAfterIntervals = 3;

    private sealed class State
    {
        public long Runs;
        public long Failures;
        public DateTimeOffset? LastSuccess;
        public DateTimeOffset? LastRun;
        public bool LastRunFailed;
        public TimeSpan Interval;
    }

    private readonly ConcurrentDictionary<string, State> jobs = new();

    public void Report(string name, bool succeeded, TimeSpan interval, DateTimeOffset now)
    {
        var state = jobs.GetOrAdd(name, _ => new State());
        lock (state)
        {
            state.Runs++;
            state.Interval = interval;
            state.LastRun = now;
            state.LastRunFailed = !succeeded;
            if (succeeded) state.LastSuccess = now;
            else state.Failures++;
        }
    }

    public IReadOnlyList<JobStatus> Snapshot(DateTimeOffset now) => jobs.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
    {
        lock (p.Value)
        {
            var s = p.Value;
            var stale = s.LastSuccess is null ? s.Failures > 0 : now - s.LastSuccess.Value > s.Interval * StaleAfterIntervals;
            return new JobStatus(p.Key, s.Runs, s.Failures, s.LastSuccess, s.LastRun, s.LastRunFailed, s.Interval, stale);
        }
    }).ToList();
}
