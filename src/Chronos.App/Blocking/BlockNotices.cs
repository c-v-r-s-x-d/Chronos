using Chronos.App.Services;
using Chronos.Ipc;

namespace Chronos.App.Blocking;

/// <summary>
/// Turns a block from the service into a window, or drops it. Kept apart from the shell: a block
/// is an interruption, and two can be true at once.
/// </summary>
public sealed class BlockNotices : IDisposable
{
    private readonly IServiceLink _link;

    private readonly BlockScreenRate _rate;

    private readonly Action<BlockEvent> _show;

    private readonly Action<Action> _post;

    /// <param name="show">Puts one block on screen. Injected so tests need no windowing system.</param>
    /// <param name="post">Hands work to the interface thread; the link raises events on its own loop.</param>
    public BlockNotices(
        IServiceLink link,
        BlockScreenRate rate,
        Action<BlockEvent> show,
        Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(rate);
        ArgumentNullException.ThrowIfNull(show);

        _link = link;
        _rate = rate;
        _show = show;
        _post = post ?? (static action => action());

        link.Blocked += OnBlocked;
    }

    /// <summary>Site notices are promised only while the DNS layer is reported and available; an unreported layer is unknown.</summary>
    public static bool SitesAreNoticed(IReadOnlyList<LayerStatus> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);

        return layers.Any(layer => layer.IsAvailable && string.Equals(layer.Name, LayerNames.Dns, StringComparison.Ordinal));
    }

    public void Dispose() => _link.Blocked -= OnBlocked;

    private void OnBlocked(object? sender, BlockEvent block) => _post(() => Consider(block));

    private void Consider(BlockEvent block)
    {
        // A status with a missing list would break a screen built from it. Not relying on the link's own check.
        if (!AnswerCheck.IsUsable(block.Status))
        {
            return;
        }

        // A block with nothing to name is not worth a screen. Not relying on the link's own check.
        if (string.IsNullOrWhiteSpace(block.Target))
        {
            return;
        }

        // Asked before the limit, so a notice refused here does not spend the domain's turn.
        if (block is SiteBlock && !SitesAreNoticed(block.Status.Layers))
        {
            return;
        }

        // Keyed by kind as well: an application and a site with one name are two targets.
        if (!_rate.Allow((block is SiteBlock ? "site:" : "app:") + block.Target))
        {
            return;
        }

        _show(block);
    }
}
