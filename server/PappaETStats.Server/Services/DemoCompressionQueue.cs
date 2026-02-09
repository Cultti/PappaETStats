using System.Threading.Channels;

namespace PappaETStats.Server.Services;

public sealed class DemoCompressionQueue
{
    public sealed record WorkItem(string ExternalMatchId, string SourcePath, string ZipPath, bool KeepOriginal);

    private readonly Channel<WorkItem> _channel = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask EnqueueAsync(WorkItem item, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(item, cancellationToken);

    public IAsyncEnumerable<WorkItem> DequeueAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
