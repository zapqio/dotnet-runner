using System.Text.Json.Serialization;

namespace Zapqio.Runner.Protocol;

/// <summary>A pulse belonging to one process and one WebSocket session.</summary>
public sealed record MessageHeartbeat
{
    [JsonRequired]
    public Guid ProcessInstanceId { get; init; }

    [JsonRequired]
    public long Sequence { get; init; }
}
