using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace LifeDrop.API.Observability;

// In-process request metrics in the Prometheus text format (plan step 3.7). Labels are the route template (never the raw path, so ids and
// customer data never become labels), the method and the status code. No package is needed; GET /metrics (token protected) renders it.
public sealed class RequestMetrics
{
    public static readonly double[] Buckets = [0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    private sealed class Series
    {
        public long Count;
        public long SumMicroseconds;
        public readonly long[] InBucket = new long[Buckets.Length + 1];   // the last one is above the largest bucket
    }

    private readonly ConcurrentDictionary<(string Route, string Method, int Status), Series> series = new();

    public void Record(string route, string method, int status, double seconds)
    {
        var s = series.GetOrAdd((route, method, status), _ => new Series());
        var index = Array.FindIndex(Buckets, b => seconds <= b);
        Interlocked.Increment(ref s.InBucket[index < 0 ? Buckets.Length : index]);
        Interlocked.Add(ref s.SumMicroseconds, (long)(seconds * 1_000_000));
        Interlocked.Increment(ref s.Count);
    }

    public long Total(string route, string method, int status) => series.TryGetValue((route, method, status), out var s) ? Interlocked.Read(ref s.Count) : 0;

    public string Render(JobHealth jobs, DateTimeOffset now)
    {
        var text = new StringBuilder();
        var ordered = series.OrderBy(p => p.Key.Route, StringComparer.Ordinal).ThenBy(p => p.Key.Method, StringComparer.Ordinal).ThenBy(p => p.Key.Status).ToList();
        text.AppendLine("# HELP lifedrop_http_requests_total Requests by route template, method and status code.");
        text.AppendLine("# TYPE lifedrop_http_requests_total counter");
        foreach (var (key, s) in ordered)
            text.AppendLine(FormattableString.Invariant($"lifedrop_http_requests_total{{{Labels(key)}}} {Interlocked.Read(ref s.Count)}"));
        text.AppendLine("# HELP lifedrop_http_request_duration_seconds Request duration.");
        text.AppendLine("# TYPE lifedrop_http_request_duration_seconds histogram");
        foreach (var (key, s) in ordered)
        {
            long cumulative = 0;
            for (var i = 0; i < Buckets.Length; i++)
            {
                cumulative += Interlocked.Read(ref s.InBucket[i]);
                text.AppendLine(FormattableString.Invariant($"lifedrop_http_request_duration_seconds_bucket{{{Labels(key)},le=\"{Buckets[i].ToString(CultureInfo.InvariantCulture)}\"}} {cumulative}"));
            }
            var count = Interlocked.Read(ref s.Count);
            text.AppendLine(FormattableString.Invariant($"lifedrop_http_request_duration_seconds_bucket{{{Labels(key)},le=\"+Inf\"}} {count}"));
            text.AppendLine(FormattableString.Invariant($"lifedrop_http_request_duration_seconds_sum{{{Labels(key)}}} {Interlocked.Read(ref s.SumMicroseconds) / 1_000_000.0}"));
            text.AppendLine(FormattableString.Invariant($"lifedrop_http_request_duration_seconds_count{{{Labels(key)}}} {count}"));
        }
        text.AppendLine("# HELP lifedrop_job_runs_total Background job runs.");
        text.AppendLine("# TYPE lifedrop_job_runs_total counter");
        var snapshot = jobs.Snapshot(now);
        foreach (var j in snapshot) text.AppendLine(FormattableString.Invariant($"lifedrop_job_runs_total{{job=\"{Escape(j.Name)}\"}} {j.Runs}"));
        text.AppendLine("# HELP lifedrop_job_failures_total Background job runs that failed.");
        text.AppendLine("# TYPE lifedrop_job_failures_total counter");
        foreach (var j in snapshot) text.AppendLine(FormattableString.Invariant($"lifedrop_job_failures_total{{job=\"{Escape(j.Name)}\"}} {j.Failures}"));
        text.AppendLine("# HELP lifedrop_job_last_success_timestamp_seconds Unix time of the last successful run (alert when it is older than three intervals).");
        text.AppendLine("# TYPE lifedrop_job_last_success_timestamp_seconds gauge");
        foreach (var j in snapshot.Where(j => j.LastSuccess is not null))
            text.AppendLine(FormattableString.Invariant($"lifedrop_job_last_success_timestamp_seconds{{job=\"{Escape(j.Name)}\"}} {j.LastSuccess!.Value.ToUnixTimeSeconds()}"));
        return text.ToString();
    }

    private static string Labels((string Route, string Method, int Status) k) => FormattableString.Invariant($"route=\"{Escape(k.Route)}\",method=\"{Escape(k.Method)}\",status=\"{k.Status}\"");

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
