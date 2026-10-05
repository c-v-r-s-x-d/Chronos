namespace Chronos.Core.Enforcement;

public enum ReconcileOutcome
{
    Unchanged,
    Changed,
    Skipped,
    Failed,
}

public sealed record ReconcileResult(
    string EnforcerName,
    ReconcileOutcome Outcome,
    int Applied,
    int Removed,
    string? Detail,
    Exception? Error = null)
{
    public static ReconcileResult Unchanged(string name) =>
        new(name, ReconcileOutcome.Unchanged, 0, 0, null);

    public static ReconcileResult Changed(string name, int applied, int removed) =>
        new(name, ReconcileOutcome.Changed, applied, removed, null);

    public static ReconcileResult Skipped(string name, string reason) =>
        new(name, ReconcileOutcome.Skipped, 0, 0, reason);

    public static ReconcileResult Failed(string name, string failure, Exception? error = null) =>
        new(name, ReconcileOutcome.Failed, 0, 0, failure, error);

    public static ReconcileResult Cleared(string name) =>
        new(name, ReconcileOutcome.Changed, 0, 0, null);
}
