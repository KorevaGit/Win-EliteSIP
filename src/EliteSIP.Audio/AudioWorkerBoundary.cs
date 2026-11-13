using System.Runtime.InteropServices;

namespace EliteSIP.Audio;

/// <summary>Граница управляемых сбоев рабочего потока, включая инициализацию.</summary>
internal static class AudioWorkerBoundary
{
    internal static bool IsRecoverableManaged(Exception error) =>
        error is not OutOfMemoryException and not AccessViolationException and not SEHException;

    internal static void Run(Action body, Action<Exception> failure)
    {
        try { body(); }
        catch (Exception error) when (IsRecoverableManaged(error))
        {
            try { failure(error); }
            catch (Exception handlerError) when (IsRecoverableManaged(handlerError))
            {
                System.Diagnostics.Trace.TraceError($"Audio failure handler failed: {handlerError}");
            }
        }
    }
}
