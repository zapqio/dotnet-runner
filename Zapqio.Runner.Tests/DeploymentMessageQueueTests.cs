using Zapqio.Runner.Background;
using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Tests;

public sealed class DeploymentMessageQueueTests
{
    [Fact]
    public async Task A_large_burst_preserves_every_message_in_order_and_removes_spill_files()
    {
        using var temp = new TemporaryDirectory();
        using var queue = new DeploymentMessageQueue(temp.Path);
        var types = new[] { MessageType.Deployment, MessageType.DeploymentApprovalResult, MessageType.DeploymentStatusAck };
        for (var n = 0; n < 5000; n++)
        {
            queue.Write(new Message { Type = types[n % types.Length], Data = n.ToString() });
        }

        Assert.NotEmpty(Directory.EnumerateFiles(temp.Path));
        queue.Complete();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var n = 0; n < 5000; n++)
        {
            var message = await queue.ReadAsync(deadline.Token);
            Assert.NotNull(message);
            Assert.Equal(types[n % types.Length], message.Type);
            Assert.Equal(n.ToString(), message.Data);
        }

        Assert.Null(await queue.ReadAsync(deadline.Token));
        Assert.Empty(Directory.EnumerateFiles(temp.Path));
    }

    [Fact]
    public async Task Interleaved_reads_and_writes_keep_order_across_multiple_file_segments()
    {
        using var temp = new TemporaryDirectory();
        using var queue = new DeploymentMessageQueue(temp.Path);
        var payload = new string('ą', 512 * 1024);
        Message Item(int n) => new() { Type = MessageType.Deployment, Data = $"{n}:{payload}" };
        for (var n = 0; n < 20; n++)
        {
            queue.Write(Item(n));
        }

        Assert.True(Directory.EnumerateFiles(temp.Path).Count() > 1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var n = 0; n < 8; n++)
        {
            Assert.Equal(Item(n).Data, (await queue.ReadAsync(deadline.Token))!.Data);
        }

        for (var n = 20; n < 30; n++)
        {
            queue.Write(Item(n));
        }

        for (var n = 8; n < 30; n++)
        {
            Assert.Equal(Item(n).Data, (await queue.ReadAsync(deadline.Token))!.Data);
        }

        Assert.Empty(Directory.EnumerateFiles(temp.Path));
        queue.Write(new Message { Type = MessageType.DeploymentStatusAck, Data = null! });
        queue.Complete();
        var last = await queue.ReadAsync(deadline.Token);
        Assert.NotNull(last);
        Assert.Equal(MessageType.DeploymentStatusAck, last.Type);
        Assert.Null(last.Data);
        Assert.Null(await queue.ReadAsync(deadline.Token));
    }

    [Fact]
    public void Disconnect_disposes_unread_spill_files()
    {
        using var temp = new TemporaryDirectory();
        using (var queue = new DeploymentMessageQueue(temp.Path))
        {
            for (var n = 0; n < 100; n++)
            {
                queue.Write(new Message { Type = MessageType.Deployment, Data = n.ToString() });
            }

            Assert.NotEmpty(Directory.EnumerateFiles(temp.Path));
        }

        Assert.Empty(Directory.EnumerateFiles(temp.Path));
    }

    [Fact]
    public async Task Waiting_reader_observes_cancellation_and_completion()
    {
        using var temp = new TemporaryDirectory();
        using var queue = new DeploymentMessageQueue(temp.Path);
        using var cancellation = new CancellationTokenSource();
        var waiting = queue.ReadAsync(cancellation.Token).AsTask();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        queue.Complete();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Null(await queue.ReadAsync(deadline.Token));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"zapq-queue-test-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path);
    }
}
