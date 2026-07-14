using System.Collections.Concurrent;
using System.Threading.Channels;

namespace WorkPlanStudio.Api.Scheduling;

public sealed class ScheduleRunQueue
{
    private readonly Channel<Guid> _queue = Channel.CreateBounded<Guid>(new BoundedChannelOptions(100)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new();

    public bool TryQueue(Guid id) => _queue.Writer.TryWrite(id);
    public bool TryRead(out Guid id) => _queue.Reader.TryRead(out id);

    public CancellationToken Register(Guid id, CancellationToken stoppingToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (!_cancellations.TryAdd(id, source))
        {
            source.Dispose();
            throw new InvalidOperationException($"Schedule run {id} is already registered.");
        }
        return source.Token;
    }

    public bool Cancel(Guid id) => _cancellations.TryGetValue(id, out var source) && TryCancel(source);

    public void Complete(Guid id)
    {
        if (_cancellations.TryRemove(id, out var source))
            source.Dispose();
    }

    private static bool TryCancel(CancellationTokenSource source)
    {
        source.Cancel();
        return true;
    }
}
