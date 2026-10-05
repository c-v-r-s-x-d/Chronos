using System.Text.Json;

namespace Chronos.Ipc;

/// <summary>Serializer settings shared by both sides of the pipe.</summary>
public static class IpcJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
