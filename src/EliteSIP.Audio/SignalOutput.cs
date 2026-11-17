using NAudio.Wave;

namespace EliteSIP.Audio;

internal interface ISignalOutput : IDisposable
{
    public void Init(ISampleProvider provider);
    public void Play();
    public void Stop();
}

internal sealed class SignalOutput(WasapiPlayer player) : ISignalOutput
{
    public void Init(ISampleProvider provider) => player.Init(provider);
    public void Play() => player.Play();
    public void Stop() => player.Stop();
    public void Dispose() => player.Dispose();
}
