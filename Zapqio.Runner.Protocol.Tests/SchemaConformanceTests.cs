using System.Text.Json.Nodes;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Protocol.Tests;

/// <summary>
/// Validates frames against <c>schemas.json</c> (Draft 2020-12), the machine-readable half of the
/// spec. Every payload schema is <c>additionalProperties: false</c> with an explicit
/// <c>required</c> list, so this catches a renamed, extra, missing or wrongly-cased field, a wrong
/// enum string, and a malformed uuid or timestamp.
/// </summary>
public class SchemaConformanceTests
{
    private static readonly Guid JobId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
    private static readonly Guid AttemptId = Guid.Parse("7f3e9c21-4b8a-4d15-9e62-0c5a7b1d8f34");

    private static string PayloadDef(string type) => type switch
    {
        "Info" => "messageInfo",
        "Deployment" => "deploymentNotice",
        "DeploymentApproval" => "deploymentApproval",
        "DeploymentApprovalResult" => "deploymentApprovalResult",
        "DeploymentStatus" => "deploymentReport",
        "DeploymentStatusAck" => "deploymentStatusAck",
        "Job" => "messageJob",
        "JobAccepted" => "messageJobAccepted",
        "JobReturn" => "messageJobReturn",
        "Log" => "messageLog",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown message type."),
    };

    /// <summary>Every message kind, built with the types under test.</summary>
    public static TheoryData<string, string> ProducedFrames() => new()
    {
        {
            "Info",
            Wire.Frame(MessageType.Info, new MessageInfo
            {
                Name = "build-agent-01",
                Methods =
                [
                    new MessageMethod
                    {
                        Name = "resize-image",
                        In = """{"type":"object"}""",
                        Out = null!,
                    },
                ],
            })
        },
        { "Job poll", Wire.PollFrame() },
        {
            "Job dispatch",
            Wire.Frame(MessageType.Job, new MessageJob
            {
                Id = JobId,
                AttemptId = AttemptId,
                Name = "resize-image",
                Data = """{"width":800}""",
            })
        },
        {
            "JobAccepted",
            Wire.Frame(MessageType.JobAccepted, new MessageJobAccepted
            {
                Id = JobId,
                AttemptId = AttemptId,
            })
        },
        {
            "Log",
            Wire.Frame(MessageType.Log, new MessageLog
            {
                JobId = JobId,
                AttemptId = AttemptId,
                Level = MessageLogLevel.Error,
                Message = "Main exception: boom",
                Date = DateTimeOffset.Parse("2026-06-12T14:30:01.456+00:00"),
            })
        },
        {
            "JobReturn OK",
            Wire.Frame(MessageType.JobReturn, new MessageJobReturn
            {
                Id = JobId,
                AttemptId = AttemptId,
                Status = MessageResponseStatus.OK,
                Data = """{"url":"https://cdn.example.com/out/123.png"}""",
            })
        },
        {
            "JobReturn ERROR",
            Wire.Frame(MessageType.JobReturn, new MessageJobReturn
            {
                Id = JobId,
                AttemptId = AttemptId,
                Status = MessageResponseStatus.ERROR,
                Data = null!,
            })
        },
    };

    [Theory]
    [MemberData(nameof(ProducedFrames))]
    public void Produced_envelope_conforms_to_the_schema(string what, string frame)
    {
        ProtocolRepo.Validate("message", JsonNode.Parse(frame), $"The produced {what} envelope");
    }

    [Theory]
    [MemberData(nameof(ProducedFrames))]
    public void Produced_payload_conforms_to_the_schema(string what, string frame)
    {
        var payload = Wire.Payload(frame);
        if (payload is null)
        {
            return; // Job poll: data is null by design (§5.2), the envelope schema already covered it.
        }

        var type = Wire.Envelope(frame)["type"]!.GetValue<string>();
        ProtocolRepo.Validate(PayloadDef(type), payload, $"The produced {what} payload");
    }

    /// <summary>Guards the harness itself: the canonical fixtures must match the canonical schemas.</summary>
    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Fixture_conforms_to_the_schema(string fixture)
    {
        var frame = ProtocolRepo.Fixture(fixture);
        ProtocolRepo.Validate("message", JsonNode.Parse(frame), $"Fixture {fixture}");

        var payload = Wire.Payload(frame);
        if (payload is not null)
        {
            var type = Wire.Envelope(frame)["type"]!.GetValue<string>();
            ProtocolRepo.Validate(PayloadDef(type), payload, $"Fixture {fixture} payload");
        }
    }

    public static TheoryData<string> FixtureNames()
    {
        var data = new TheoryData<string>();
        foreach (var fixture in ProtocolRepo.AllFixtures)
        {
            data.Add(fixture);
        }

        return data;
    }
}
