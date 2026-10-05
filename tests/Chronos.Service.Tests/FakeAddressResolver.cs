using System.Net;
using Chronos.Core.Rules;
using Chronos.Service.Wfp;

namespace Chronos.Service.Tests;

/// <summary>Scripted answers for the pre-resolver, one per pass. When the script runs out it answers with nothing, as a real resolve does when no server replies.</summary>
internal sealed class FakeAddressResolver : IAddressResolver
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<IPAddress>> Nothing =
        new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.Ordinal);

    private readonly Queue<IReadOnlyDictionary<string, IReadOnlyList<IPAddress>>> _answers = new();

    /// <summary>How many passes actually reached the resolver (one per ten minutes).</summary>
    public int Calls { get; private set; }

    /// <summary>The domain set each pass asked about, in call order.</summary>
    public List<string[]> AskedFor { get; } = [];

    public Exception? Throws { get; set; }

    public void Enqueue(params (string Domain, string[] Addresses)[] entries)
    {
        var answer = new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.Ordinal);
        foreach (var (domain, addresses) in entries)
        {
            answer[domain] = [.. addresses.Select(IPAddress.Parse)];
        }

        _answers.Enqueue(answer);
    }

    public Task<IReadOnlyDictionary<string, IReadOnlyList<IPAddress>>> ResolveAsync(
        IEnumerable<SiteRule> sites, CancellationToken ct)
    {
        Calls++;
        AskedFor.Add([.. sites.Select(site => site.Domain)]);

        if (Throws is { } failure)
        {
            throw failure;
        }

        return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : Nothing);
    }
}
