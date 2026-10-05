using Chronos.Core.Sessions;
using Chronos.Service.Ipc;
using Chronos.Service.Reconciliation;
using Chronos.Service.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chronos.Service;

public sealed class ChronosWorker(
    SessionEngine engine,
    ReconcileScheduler scheduler,
    IpcServer ipc,
    StateStore state,
    ILogger<ChronosWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Chronos service started; restored state is {State}.",
            engine.State);

        var observed = ObserveIpcAsync(ipc.RunAsync(stoppingToken));

        try
        {
            // Reconcile at startup so a restored session is re-applied right after a reboot.
            try
            {
                await scheduler.RunPendingAsync("startup", stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // A failed pass must not stop the service; the next one retries from the same state.
                logger.LogError(exception, "A reconcile pass failed and was skipped.");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var reason = await scheduler.WaitForNextTriggerAsync(stoppingToken).ConfigureAwait(false);

                try
                {
                    await scheduler.RunPendingAsync(reason, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // A failed pass must not stop the service; the next one retries from the same state.
                    logger.LogError(exception, "A reconcile pass failed and was skipped.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        // Stopping the service does not end the session; it resumes on the next start.
        if (engine.Session is { } session)
        {
            state.Save(session);
        }

        logger.LogInformation("Chronos service stopped.");

        await observed.ConfigureAwait(false);
    }

    private async Task ObserveIpcAsync(Task serving)
    {
        try
        {
            await serving.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The IPC server stopped unexpectedly; the service can no longer accept commands.");
        }
    }
}
