namespace EliteSIP.Audio;

/// <summary>Silent RTP timing follows rendered sample counts, without another timer thread.</summary>
internal sealed class ReceiveOnlyClock(int sampleRate, int packetMilliseconds)
{
    private long _pending;
    internal int Advance(int renderedFrames)
    {
        _pending += (long)renderedFrames * 1000;
        var packet = (long)sampleRate * packetMilliseconds;
        var count = (int)(_pending / packet);
        _pending %= packet;
        return count;
    }
}
