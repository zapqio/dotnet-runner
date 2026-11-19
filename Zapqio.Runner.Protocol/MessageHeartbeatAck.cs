using System.Text.Json.Serialization;

namespace Zapqio.Runner.Protocol;

/// <summary>Echoes the process and sequence of a pulse received by Web.</summary>
public sealed record MessageHeartbeatAck
{
    [JsonRequired]
    public Guid ProcessInstanceId { get; init; }

    [JsonRequired]
    public long Sequence { get; init; }
}
