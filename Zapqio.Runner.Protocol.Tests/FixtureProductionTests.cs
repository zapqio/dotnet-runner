using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Protocol.Tests;

/// <summary>
/// The other half of §10 conformance: building each documented message with the types in
/// Zapqio.Runner.Protocol must <b>produce</b> a frame semantically equal to the fixture — same
/// field names, same enum strings, same double-encoding of <c>data</c>.
/// </summary>
public class FixtureProductionTests
{
    private static readonly Guid JobId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
    private static readonly Guid AttemptId = Guid.Parse("7f3e9c21-4b8a-4d15-9e62-0c5a7b1d8f34");

    private const string ResizeImageInputSchema =
        """{"type":"object","properties":{"width":{"type":"integer"},"height":{"type":"integer"}}}""";

    private const string ResizeImageOutputSchema =
        """{"type":"object","properties":{"url":{"type":"string"}}}""";

    [Fact]
    public void Info_frame_matches_the_fixture()
    {
        var frame = Wire.Frame(MessageType.Info, new MessageInfo
        {
            Name = "build-agent-01",
            ProcessInstanceId = Guid.Parse("8dbe4462-6baf-4a07-91f6-8aa2753a27e1"),
            ActiveAttemptIds = [],
            MaxConcurrency = 4,
            Methods =
            [
                new MessageMethod
                {
                    Name = "resize-image",
                    In = ResizeImageInputSchema,
                    Out = ResizeImageOutputSchema,
                },
            ],
        });

        Wire.AssertSameMessage(ProtocolRepo.Fixture("info.json"), frame, "The produced Info frame");
    }

    [Fact]
    public void Job_poll_frame_matches_the_fixture()
    {
        Wire.AssertSameMessage(ProtocolRepo.Fixture("job-poll.json"), Wire.PollFrame(), "The produced Job poll frame");
    }

    [Fact]
    public void Job_dispatch_frame_matches_the_fixture()
    {
        var frame = Wire.Frame(MessageType.Job, new MessageJob
        {
            Id = JobId,
            AttemptId = AttemptId,
            Name = "resize-image",
            Data = """{"width":800,"height":600}""",
        });

        Wire.AssertSameMessage(ProtocolRepo.Fixture("job-dispatch.json"), frame, "The produced Job dispatch frame");
    }

    [Fact]
    public void Job_accepted_frame_matches_the_fixture()
    {
        var frame = Wire.Frame(MessageType.JobAccepted, new MessageJobAccepted
        {
            Id = JobId,
            AttemptId = AttemptId,
        });

        Wire.AssertSameMessage(
            ProtocolRepo.Fixture("job-accepted.json"), frame, "The produced JobAccepted frame");
    }

    [Theory]
    [InlineData("log-info.json", MessageLogLevel.Info, "Run Job: 2026-06-12T14:30:00", "2026-06-12T14:30:00.123+00:00")]
    [InlineData("log-error.json", MessageLogLevel.Error, "Main exception: boom", "2026-06-12T14:30:01.456+00:00")]
    // Poziomy dodane w v3 protokolu (§9).
    [InlineData("log-debug.json", MessageLogLevel.Debug, "Cache hit: resize-image/800x600", "2026-06-12T14:30:00.456+00:00")]
    [InlineData("log-warning.json", MessageLogLevel.Warning, "Upstream answered in 4s, over the 2s budget", "2026-06-12T14:30:00.789+00:00")]
    public void Log_frame_matches_the_fixture(string fixture, MessageLogLevel level, string message, string date)
    {
        var frame = Wire.Frame(MessageType.Log, new MessageLog
        {
            JobId = JobId,
            AttemptId = AttemptId,
            Level = level,
            Message = message,
            Date = DateTimeOffset.Parse(date),
        });

        Wire.AssertSameMessage(ProtocolRepo.Fixture(fixture), frame, $"The produced {level} Log frame");
    }

    [Fact]
    public void JobReturn_ok_frame_matches_the_fixture()
    {
        var frame = Wire.Frame(MessageType.JobReturn, new MessageJobReturn
        {
            Id = JobId,
            AttemptId = AttemptId,
            Status = MessageResponseStatus.OK,
            Data = """{"url":"https://cdn.example.com/out/123.png"}""",
        });

        Wire.AssertSameMessage(ProtocolRepo.Fixture("job-return-ok.json"), frame, "The produced OK JobReturn frame");
    }

    [Fact]
    public void JobReturn_error_frame_matches_the_fixture()
    {
        var frame = Wire.Frame(MessageType.JobReturn, new MessageJobReturn
        {
            Id = JobId,
            AttemptId = AttemptId,
            Status = MessageResponseStatus.ERROR,
            Data = null!,
        });

        Wire.AssertSameMessage(
            ProtocolRepo.Fixture("job-return-error.json"), frame, "The produced ERROR JobReturn frame");
    }

    /// <summary>
    /// §4: <c>data</c> is a string holding JSON, never a nested object. A producer that forgets the
    /// extra encoding still round-trips against itself, so this is checked on the raw frame.
    /// </summary>
    [Fact]
    public void Envelope_data_is_a_string_not_a_nested_object()
    {
        var frame = Wire.Frame(MessageType.JobReturn, new MessageJobReturn
        {
            Id = JobId,
            Status = MessageResponseStatus.OK,
            Data = """{"url":"x"}""",
        });

        var data = Wire.Envelope(frame)["data"];

        Assert.NotNull(data);
        Assert.Equal(System.Text.Json.JsonValueKind.String, data!.GetValueKind());
    }
}
