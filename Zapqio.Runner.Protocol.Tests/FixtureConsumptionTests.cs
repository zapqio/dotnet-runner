using System.Text.Json;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Protocol.Tests;

/// <summary>
/// PROTOCOL.md §10: an implementation conforms if it can <b>consume</b> every fixture such that,
/// after full decoding, the logical content equals the fixture's expected content.
/// These tests read every canonical frame with the types in
/// Zapqio.Runner.Protocol and check the values that come out.
/// </summary>
public class FixtureConsumptionTests
{
    private const string JobId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string AttemptId = "7f3e9c21-4b8a-4d15-9e62-0c5a7b1d8f34";

    private static TPayload Decode<TPayload>(string fixture, MessageType expectedType)
    {
        var envelope = JsonSerializer.Deserialize<Message>(ProtocolRepo.Fixture(fixture), JsonDefaults.Options);
        Assert.NotNull(envelope);
        Assert.Equal(expectedType, envelope!.Type);
        Assert.NotNull(envelope.Data);

        var payload = JsonSerializer.Deserialize<TPayload>(envelope.Data, JsonDefaults.Options);
        Assert.NotNull(payload);
        return payload!;
    }

    [Theory]
    [InlineData("info.json", MessageType.Info)]
    [InlineData("info-sequential.json", MessageType.Info)]
    [InlineData("job-poll.json", MessageType.Job)]
    [InlineData("job-dispatch.json", MessageType.Job)]
    [InlineData("job-accepted.json", MessageType.JobAccepted)]
    [InlineData("log-info.json", MessageType.Log)]
    [InlineData("log-error.json", MessageType.Log)]
    [InlineData("log-debug.json", MessageType.Log)]
    [InlineData("log-warning.json", MessageType.Log)]
    [InlineData("job-return-ok.json", MessageType.JobReturn)]
    [InlineData("job-return-error.json", MessageType.JobReturn)]
    public void Envelope_of_every_fixture_is_readable(string fixture, MessageType expectedType)
    {
        var envelope = JsonSerializer.Deserialize<Message>(ProtocolRepo.Fixture(fixture), JsonDefaults.Options);

        Assert.NotNull(envelope);
        Assert.Equal(expectedType, envelope!.Type);
    }

    [Fact]
    public void Info_announces_the_runner_name_and_its_methods()
    {
        var info = Decode<MessageInfo>("info.json", MessageType.Info);

        Assert.Equal("build-agent-01", info.Name);
        Assert.Equal(4, info.MaxConcurrency);
        var method = Assert.Single(info.Methods);
        Assert.Equal("resize-image", method.Name);

        // `in` and `out` carry a JSON Schema *as a string* (§7).
        using var inputSchema = JsonDocument.Parse(method.In);
        Assert.Equal("object", inputSchema.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            "integer",
            inputSchema.RootElement.GetProperty("properties").GetProperty("width").GetProperty("type").GetString());

        // The output describes what job-return-ok.json returns - the fixtures are one example.
        using var outputSchema = JsonDocument.Parse(method.Out);
        Assert.Equal("object", outputSchema.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            "string",
            outputSchema.RootElement.GetProperty("properties").GetProperty("url").GetProperty("type").GetString());
    }

    /// <summary>
    /// A runner from before the maxConcurrency field (or one that runs a single job at a time) does
    /// not send it - a consumer MUST read that as capacity 1 (§5.1, §9).
    /// </summary>
    [Fact]
    public void Info_without_maxConcurrency_means_one_job_at_a_time()
    {
        var info = Decode<MessageInfo>("info-sequential.json", MessageType.Info);

        Assert.Equal("build-agent-01", info.Name);
        Assert.Single(info.Methods);
        Assert.Equal(1, info.MaxConcurrency);
    }

    [Fact]
    public void Job_poll_carries_no_payload()
    {
        var envelope = JsonSerializer.Deserialize<Message>(ProtocolRepo.Fixture("job-poll.json"), JsonDefaults.Options);

        Assert.NotNull(envelope);
        Assert.Equal(MessageType.Job, envelope!.Type);
        Assert.Null(envelope.Data);
    }

    [Fact]
    public void Job_dispatch_decodes_through_both_string_layers()
    {
        var job = Decode<MessageJob>("job-dispatch.json", MessageType.Job);

        Assert.Equal(Guid.Parse(JobId), job.Id);
        Assert.Equal(Guid.Parse(AttemptId), job.AttemptId);
        Assert.Equal("resize-image", job.Name);

        // §8: MessageJob.data is itself a JSON string — the third decoding level.
        using var input = JsonDocument.Parse(job.Data);
        Assert.Equal(800, input.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(600, input.RootElement.GetProperty("height").GetInt32());
    }

    [Theory]
    [InlineData("log-info.json", MessageLogLevel.Info, "Run Job: 2026-06-12T14:30:00", "2026-06-12T14:30:00.123+00:00")]
    [InlineData("log-error.json", MessageLogLevel.Error, "Main exception: boom", "2026-06-12T14:30:01.456+00:00")]
    // Poziomy dodane w v3 protokolu (§9).
    [InlineData("log-debug.json", MessageLogLevel.Debug, "Cache hit: resize-image/800x600", "2026-06-12T14:30:00.456+00:00")]
    [InlineData("log-warning.json", MessageLogLevel.Warning, "Upstream answered in 4s, over the 2s budget", "2026-06-12T14:30:00.789+00:00")]
    public void Log_decodes_level_message_and_timestamp(
        string fixture, MessageLogLevel level, string message, string date)
    {
        var log = Decode<MessageLog>(fixture, MessageType.Log);

        Assert.Equal(Guid.Parse(JobId), log.JobId);
        Assert.Equal(Guid.Parse(AttemptId), log.AttemptId);
        Assert.Equal(level, log.Level);
        Assert.Equal(message, log.Message);
        Assert.Equal(DateTimeOffset.Parse(date), log.Date);
    }

    [Fact]
    public void JobReturn_ok_carries_the_job_output()
    {
        var result = Decode<MessageJobReturn>("job-return-ok.json", MessageType.JobReturn);

        Assert.Equal(Guid.Parse(JobId), result.Id);
        Assert.Equal(Guid.Parse(AttemptId), result.AttemptId);
        Assert.Equal(MessageResponseStatus.OK, result.Status);

        using var output = JsonDocument.Parse(result.Data);
        Assert.Equal("https://cdn.example.com/out/123.png", output.RootElement.GetProperty("url").GetString());
    }

    [Fact]
    public void JobReturn_error_carries_no_output()
    {
        var result = Decode<MessageJobReturn>("job-return-error.json", MessageType.JobReturn);

        Assert.Equal(Guid.Parse(JobId), result.Id);
        Assert.Equal(Guid.Parse(AttemptId), result.AttemptId);
        Assert.Equal(MessageResponseStatus.ERROR, result.Status);
        Assert.Null(result.Data);
    }

    /// <summary>
    /// §5.3: the acknowledgement carries nothing but the (job, attempt) pair. That pair is how Web
    /// learns the dispatch arrived, so both fields must survive both encoding levels.
    /// </summary>
    [Fact]
    public void Job_accepted_carries_the_job_and_the_attempt()
    {
        var accepted = Decode<MessageJobAccepted>("job-accepted.json", MessageType.JobAccepted);

        Assert.Equal(Guid.Parse(JobId), accepted.Id);
        Assert.Equal(Guid.Parse(AttemptId), accepted.AttemptId);
    }
}
