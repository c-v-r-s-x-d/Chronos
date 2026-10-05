using System.Text.Json;
using Chronos.Core.Sessions;
using Chronos.Service.Configuration;
using Microsoft.Extensions.Logging;

namespace Chronos.Service.State;

public sealed class StateStore(ChronosPaths paths, ILogger<StateStore> logger)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public BlockSession? Load()
    {
        if (!File.Exists(paths.StateFile))
        {
            return null;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<SessionDto>(File.ReadAllText(paths.StateFile), Options);
            if (dto is null)
            {
                return null;
            }

            if (!dto.IsComplete())
            {
                // Valid JSON but no usable session; a partial one would strand the engine.
                DeleteUnusableFile("Session state is incomplete and will be discarded and deleted.");
                return null;
            }

            return dto.ToDomain();
        }
        catch (Exception exception) when (exception is JsonException or IOException or ArgumentException)
        {
            // Unreadable state: the caller clears everything and starts from Idle.
            DeleteUnusableFile("Session state is unreadable and will be discarded and deleted.", exception);
            return null;
        }
    }

    public void Save(BlockSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        AtomicFile.Write(paths.StateFile, JsonSerializer.Serialize(SessionDto.FromDomain(session), Options));
    }

    public void Clear()
    {
        if (File.Exists(paths.StateFile))
        {
            File.Delete(paths.StateFile);
        }
    }

    private void DeleteUnusableFile(string message, Exception? exception = null)
    {
        logger.LogError(exception, message);

        try
        {
            File.Delete(paths.StateFile);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Load runs during service registration; throwing would stop the service starting.
            // Clear() does not swallow, so its caller retries on the next pass.
            logger.LogWarning(failure, "The unusable session state file could not be removed.");
        }
    }
}
