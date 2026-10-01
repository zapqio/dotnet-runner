using System.Text.Json;
using System.Text.Json.Nodes;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Protocol.Tests;

/// <summary>
/// PROTOCOL.md §4/§5: every payload carries exactly the documented fields, all camelCase. Each type
/// in Zapqio.Runner.Protocol is serialized with every property populated and its field names are
/// compared with the <c>properties</c> of the matching schema — a missing, extra or renamed field
/// fails here rather than at a customer's server.
/// </summary>
public class PayloadShapeTests
{
    public static TheoryData<string, object> Payloads() => new()
    {
        {
            "message",
            new Message { Type = MessageType.Job, Data = "{}" }
        },
        {
            "messageInfo",
            new MessageInfo { Name = "build-agent-01", Methods = [], ProcessInstanceId = Guid.NewGuid(), ActiveAttemptIds = [] }
        },
        {
            "messageMethod",
            new MessageMethod { Name = "resize-image", In = "{}", Out = "{}" }
        },
        {
            "messageJob",
            new MessageJob { Id = Guid.NewGuid(), Name = "resize-image", Data = "{}" }
        },
        {
            "messageJobReturn",
            new MessageJobReturn { Id = Guid.NewGuid(), Status = MessageResponseStatus.OK, Data = "{}", ExecutionVersion = new(Guid.NewGuid(), Guid.NewGuid(), new string('a', 40)) }
        },
        {
            "messageLog",
            new MessageLog
            {
                JobId = Guid.NewGuid(),
                Level = MessageLogLevel.Info,
                Message = "hello",
                ExecutionVersion = new(Guid.NewGuid(), Guid.NewGuid(), new string('a', 40)),
                Date = DateTimeOffset.UtcNow,
            }
        },
    };

    [Theory]
    [MemberData(nameof(Payloads))]
    public void Serialized_fields_are_exactly_the_documented_ones(string def, object payload)
    {
        var serialized = JsonNode.Parse(JsonSerializer.Serialize(payload, payload.GetType(), JsonDefaults.Options))!
            .AsObject()
            .Select(property => property.Key)
            .OrderBy(name => name, StringComparer.Ordinal);

        var documented = ProtocolRepo.Defs[def]!["properties"]!
            .AsObject()
            .Select(property => property.Key)
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(documented, serialized);
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void Required_fields_are_always_written(string def, object payload)
    {
        var serialized = JsonNode.Parse(JsonSerializer.Serialize(payload, payload.GetType(), JsonDefaults.Options))!
            .AsObject();

        foreach (var required in ProtocolRepo.Defs[def]!["required"]!.AsArray().Select(v => v!.GetValue<string>()))
        {
            Assert.True(
                serialized.ContainsKey(required),
                $"{def}: required field '{required}' is missing from the serialized payload {serialized.ToJsonString()}.");
        }
    }

    /// <summary>
    /// §4: "All object property names are camelCase". The naming policy lives in JsonDefaults, so a
    /// payload serialized without those options would silently go out PascalCase.
    /// </summary>
    [Theory]
    [MemberData(nameof(Payloads))]
    public void Field_names_are_camelCase(string def, object payload)
    {
        _ = def;
        var serialized = JsonNode.Parse(JsonSerializer.Serialize(payload, payload.GetType(), JsonDefaults.Options))!
            .AsObject();

        foreach (var property in serialized)
        {
            Assert.True(
                char.IsLower(property.Key[0]),
                $"Field '{property.Key}' of {payload.GetType().Name} is not camelCase.");
        }
    }

    /// <summary>
    /// §5.1/§5.4: <c>out</c>, and a JobReturn's <c>data</c> on ERROR, are null — the property must
    /// still be present, because the schema requires it.
    /// </summary>
    [Fact]
    public void Null_valued_fields_are_still_written()
    {
        var method = JsonNode.Parse(JsonSerializer.Serialize(
            new MessageMethod { Name = "resize-image", In = null!, Out = null! }, JsonDefaults.Options))!.AsObject();

        Assert.True(method.ContainsKey("in"));
        Assert.True(method.ContainsKey("out"));
        Assert.Null(method["in"]);
        Assert.Null(method["out"]);

        var result = JsonNode.Parse(JsonSerializer.Serialize(
            new MessageJobReturn { Id = Guid.NewGuid(), Status = MessageResponseStatus.ERROR, Data = null! },
            JsonDefaults.Options))!.AsObject();

        Assert.True(result.ContainsKey("data"));
        Assert.Null(result["data"]);
    }

    /// <summary>
    /// §6 note: the reference deserializer is lenient on read. Kept as a characterization test so
    /// tightening it stays a deliberate decision.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"job","data":null}""")]
    [InlineData("""{"TYPE":"Job","DATA":null}""")]
    public void Deserializer_is_lenient_about_casing_on_read(string frame)
    {
        var envelope = JsonSerializer.Deserialize<Message>(frame, JsonDefaults.Options);

        Assert.NotNull(envelope);
        Assert.Equal(MessageType.Job, envelope!.Type);
    }
}
