using System.Reflection;
using System.Runtime.InteropServices;

namespace EliteSIP.App.Shell;

/// <summary>
/// Что делать с исключением, которое никто не поймал, — пережить или упасть.
/// </summary>
///
/// <remarks>
/// <para>
/// До 0.1.67 любое непойманное исключение в потоке интерфейса закрывало
/// софтфон: обработчик писал <c>crash.log</c> и отпускал исключение дальше. А
/// команд на <c>async</c>-лямбдах в приложении с десяток — удержание, перевод,
/// конференция, «Не беспокоить», — и исключение в любой из них приходит именно
/// туда. Сам софтфон после этого не поднимался: оператор узнавал о падении,
/// когда переставали идти звонки.
/// </para>
/// <para>
/// Теперь исключение в интерфейсе переживается — кроме двух случаев, когда
/// жить дальше хуже, чем упасть. Первый — исключения, после которых процесс
/// испорчен (нехватка памяти, сломанная статическая инициализация, повреждение
/// памяти). Второй — серия: больше <see cref="BurstLimit"/> за
/// <see cref="BurstWindow"/> означает, что ошибка повторяется на каждом такте
/// отрисовки или таймера, и «пережить» её значит повесить окно в цикле. В обоих
/// случаях процесс падает, а поднимает его система: Windows — по
/// <c>CrashRestart.Register</c> в WPF-приложении, Linux — служба systemd.
/// </para>
/// <para>
/// Решение от ОС не зависит и с 21 октября 2026 живёт в App.Core: его
/// разделяют обе версии.
/// </para>
/// </remarks>
internal sealed class CrashGuard
{
    internal const int BurstLimit = 5;

    internal static readonly TimeSpan BurstWindow = TimeSpan.FromMinutes(1);

    private readonly Queue<long> _recent = new();
    private readonly Func<long> _now;
    private readonly Lock _gate = new();

    /// <param name="now">Часы в миллисекундах. Подменяются в проверках.</param>
    internal CrashGuard(Func<long>? now = null)
    {
        _now = now ?? (() => Environment.TickCount64);
    }

    /// <summary>Пережить ли это исключение.</summary>
    internal bool ShouldSurvive(Exception error)
    {
        if (IsFatal(error))
        {
            return false;
        }

        lock (_gate)
        {
            var now = _now();
            while (_recent.Count > 0 && now - _recent.Peek() > (long)BurstWindow.TotalMilliseconds)
            {
                _recent.Dequeue();
            }

            _recent.Enqueue(now);
            return _recent.Count <= BurstLimit;
        }
    }

    /// <summary>Исключение, после которого процесс уже нельзя считать исправным.</summary>
    internal static bool IsFatal(Exception error) => error switch
    {
        TargetInvocationException { InnerException: { } inner } => IsFatal(inner),
        OutOfMemoryException
            or InsufficientExecutionStackException
            or AccessViolationException
            or SEHException
            or InvalidProgramException
            or BadImageFormatException
            or TypeInitializationException => true,
        _ => false,
    };
}
