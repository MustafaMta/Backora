using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Backora.Application.Telemetry;

/// <summary>Custom OpenTelemetry instruments. Tags are low-cardinality (kind, outcome, failure class) — never job names or ids.</summary>
public static class BackoraTelemetry
{
    public const string ServiceName = "Backora";
    public static readonly ActivitySource ActivitySource = new("Backora");
    public static readonly Meter Meter = new("Backora", "2.0.0");

    public static readonly Counter<long> OperationsStarted = Meter.CreateCounter<long>("backora.operations.started", description: "Background operations started");
    public static readonly Counter<long> OperationsCompleted = Meter.CreateCounter<long>("backora.operations.completed", description: "Background operations reaching a terminal state");
    public static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>("backora.operations.duration", unit: "s", description: "Duration of terminal operations");
    public static readonly Counter<long> BytesTransferred = Meter.CreateCounter<long>("backora.transfer.bytes", unit: "By", description: "Bytes verified in object storage");
    public static readonly Counter<long> LeaseLost = Meter.CreateCounter<long>("backora.leases.lost", description: "Operations whose lease was lost");
    public static readonly Counter<long> NotificationsSent = Meter.CreateCounter<long>("backora.notifications", description: "Notification dispatch attempts");
    public static readonly Counter<long> RetentionDeleted = Meter.CreateCounter<long>("backora.retention.deleted", description: "Recovery points deleted by retention");
    public static readonly Counter<long> ScheduledRuns = Meter.CreateCounter<long>("backora.scheduler.runs", description: "Scheduler decisions");

    public static void RecordTerminal(string kind, string outcome, string failureClass, TimeSpan? duration)
    {
        var tags = new TagList { { "kind", kind }, { "outcome", outcome }, { "failure_class", failureClass } };
        OperationsCompleted.Add(1, tags);
        if (duration is { } d) OperationDuration.Record(d.TotalSeconds, new TagList { { "kind", kind }, { "outcome", outcome } });
    }
}
