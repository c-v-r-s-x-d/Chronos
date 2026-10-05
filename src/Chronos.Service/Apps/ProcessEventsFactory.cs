using Microsoft.Extensions.Logging;

namespace Chronos.Service.Apps;

public static class ProcessEventsFactory
{
    /// <summary>
    /// Logged when the WMI source takes over. The diagnostic package finds the fallback by this
    /// exact text, so do not reword it.
    /// </summary>
    public const string FellBackToWmi =
        "The kernel trace session could not be started; falling back to the WMI process source.";

    /// <summary>
    /// Starts the trace session source, or falls back to WMI and logs it. WMI is slower and cannot
    /// match full paths.
    /// </summary>
    public static IProcessEvents Create(
        Action<ProcessStarted> onStarted,
        Func<IProcessEvents> createPrimary,
        Func<IProcessEvents> createFallback,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(onStarted);
        ArgumentNullException.ThrowIfNull(createPrimary);
        ArgumentNullException.ThrowIfNull(createFallback);
        ArgumentNullException.ThrowIfNull(logger);

        var primary = createPrimary();

        try
        {
            primary.Start(onStarted);
            return primary;
        }
        catch (Exception exception)
        {
            primary.Dispose();
            logger.LogInformation(exception, FellBackToWmi);
        }

        var fallback = createFallback();

        try
        {
            fallback.Start(onStarted);
        }
        catch (Exception exception)
        {
            // Both sources failed; record the second failure too.
            logger.LogError(exception, "The WMI process source could not be started either.");
            fallback.Dispose();
            throw;
        }

        return fallback;
    }
}
