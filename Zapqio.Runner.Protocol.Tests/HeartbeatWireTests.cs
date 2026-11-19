using System.Text.Json;
using System.Text.Json.Nodes;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Protocol.Tests;

public sealed class HeartbeatWireTests
{
    private static readonly Guid Process = Guid.Parse("8dbe4462-6baf-4a07-91f6-8aa2753a27e1");

    [Theory]
    [InlineData(MessageType.Heartbeat, "heartbeat.json", "messageHeartbeat")]
    [InlineData(MessageType.HeartbeatAck, "heartbeat-ack.json", "messageHeartbeatAck")]
    public void Both_heartbeat_messages_produce_and_consume_the_canonical_frame(MessageType type, string fixture, string definition)
    {
        object payload = type == MessageType.Heartbeat
            ? new MessageHeartbeat { ProcessInstanceId = Process, Sequence = 1 }
            : new MessageHeartbeatAck { ProcessInstanceId = Process, Sequence = 1 };
        var frame = Wire.Frame(type, payload);
        Wire.AssertSameMessage(ProtocolRepo.Fixture(fixture), frame, type.ToString());
        ProtocolRepo.Validate("message", JsonNode.Parse(frame), type.ToString());
        ProtocolRepo.Validate(definition, Wire.Payload(frame), type.ToString());
        var envelope = JsonSerializer.Deserialize<Message>(ProtocolRepo.Fixture(fixture), JsonDefaults.Options)!;
        Assert.Equal(type, envelope.Type);
        if (type == MessageType.Heartbeat)
        {
            var consumed = JsonSerializer.Deserialize<MessageHeartbeat>(envelope.Data, JsonDefaults.Options)!;
            Assert.Equal(Process, consumed.ProcessInstanceId);
            Assert.Equal(1, consumed.Sequence);
        }
        else
        {
            var consumed = JsonSerializer.Deserialize<MessageHeartbeatAck>(envelope.Data, JsonDefaults.Options)!;
            Assert.Equal(Process, consumed.ProcessInstanceId);
            Assert.Equal(1, consumed.Sequence);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"sequence\":1}")]
    [InlineData("{\"processInstanceId\":\"8dbe4462-6baf-4a07-91f6-8aa2753a27e1\"}")]
    [InlineData("{\"processInstanceId\":\"invalid\",\"sequence\":1}")]
    [InlineData("{\"processInstanceId\":\"8dbe4462-6baf-4a07-91f6-8aa2753a27e1\",\"sequence\":1.5}")]
    [InlineData("{\"processInstanceId\":\"8dbe4462-6baf-4a07-91f6-8aa2753a27e1\",\"sequence\":\"1\"}")]
    public void Malformed_heartbeat_fields_are_not_silently_defaulted(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MessageHeartbeat>(json, JsonDefaults.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MessageHeartbeatAck>(json, JsonDefaults.Options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(9007199254740992L)]
    public void Schema_rejects_numbers_outside_the_wire_range(long sequence)
    {
        var payload = JsonNode.Parse(JsonSerializer.Serialize(new MessageHeartbeat { ProcessInstanceId = Process, Sequence = sequence }, JsonDefaults.Options));
        foreach (var definition in new[] { "messageHeartbeat", "messageHeartbeatAck" })
        {
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => ProtocolRepo.Validate(definition, payload, "invalid sequence"));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9007199254740991L)]
    public void Exact_sequence_boundaries_survive_serialization(long sequence)
    {
        var pulse = new MessageHeartbeat { ProcessInstanceId = Process, Sequence = sequence };
        var encoded = JsonSerializer.Serialize(pulse, JsonDefaults.Options);
        Assert.Equal(pulse, JsonSerializer.Deserialize<MessageHeartbeat>(encoded, JsonDefaults.Options));
        ProtocolRepo.Validate("messageHeartbeat", JsonNode.Parse(encoded), "boundary pulse");
    }

    [Theory]
    [InlineData("{\"name\":\"test\",\"methods\":[],\"activeAttemptIds\":[]}")]
    [InlineData("{\"name\":\"test\",\"methods\":[],\"processInstanceId\":\"8dbe4462-6baf-4a07-91f6-8aa2753a27e1\"}")]
    public void V4_info_requires_process_and_reconciliation_state(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MessageInfo>(json, JsonDefaults.Options));
    }
}
