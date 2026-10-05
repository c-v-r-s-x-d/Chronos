namespace Chronos.Service.Diagnostics;

/// <summary>
/// Tells whether a diagnostic is news: a first occurrence or different from the last. A null
/// signature means the condition is absent.
/// </summary>
internal sealed class RepeatedDiagnostic
{
    private readonly Lock _sync = new();
    private string? _reported;

    public bool IsNews(string? signature)
    {
        lock (_sync)
        {
            var news = signature is not null && !string.Equals(signature, _reported, StringComparison.Ordinal);
            _reported = signature;

            return news;
        }
    }
}
