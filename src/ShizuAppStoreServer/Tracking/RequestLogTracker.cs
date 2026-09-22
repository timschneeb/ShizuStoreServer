using System.Threading.Channels;

namespace ShizuAppStoreServer.Tracking;

/// <summary>
/// Non-blocking buffer between the request pipeline and the flush worker.
/// Recording must never fail or stall a request, so a full buffer drops hits
/// (counted, logged by the worker) instead of applying backpressure.
/// </summary>
public sealed class RequestLogTracker
{
    private readonly Channel<RequestLogHit> _channel;
    private long _dropped;

    public RequestLogTracker(RequestLogOptions options)
    {
        _channel = Channel.CreateBounded<RequestLogHit>(new BoundedChannelOptions(
            Math.Max(1, options.MaxBufferedHits))
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public ChannelReader<RequestLogHit> Reader => _channel.Reader;

    public void Record(in RequestLogHit hit)
    {
        if (!_channel.Writer.TryWrite(hit))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Reads and resets the dropped counter so the worker logs each drop once.</summary>
    public long ConsumeDropped() => Interlocked.Exchange(ref _dropped, 0);
}
