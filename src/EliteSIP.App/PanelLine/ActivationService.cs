using System.Net;
using System.Net.Http;
using EliteSIP.PanelLink;

namespace EliteSIP.App.PanelLine;

/// <summary>
/// Забрать пакет активации по ключу.
/// </summary>
///
/// <remarks>
/// Единственный запрос, который приложение делает при активации, — и идёт он
/// <b>не в панель</b>, а на тот же канал раздачи, что и обновления. Панель стоит
/// на локальном сервере конторы и наружу не смотрит; сотрудник из дома до неё не
/// достаёт и достать не должен.
///
/// Адрес пакета выводится из самого ключа, поэтому сервера посередине не нужно:
/// его считают обе стороны одинаково. Знание адреса при этом ничего не даёт —
/// пакет по нему лежит зашифрованный тем же ключом.
///
/// <b>Ключ нигде не сохраняется.</b> Он одноразовый, и второй раз пакет не
/// отдадут: Worker перед бакетом столбит его за первым забравшим.
/// </remarks>
internal static class ActivationService
{
    /// <summary>
    /// Забирает и распечатывает пакет.
    /// </summary>
    ///
    /// <remarks>
    /// Отказы не различают неверный ключ и испорченный файл — так решено в
    /// <c>PanelLink</c>: подбирающему незачем знать, где он ошибся.
    /// </remarks>
    ///
    /// <param name="key">то, что ввёл человек.</param>
    /// <param name="installationID">
    /// машина, если это ключ перепрошивки. <c>null</c> при первой активации.
    /// </param>
    internal static async Task<ActivationPackage> FetchAsync(
        ActivationKey key, string? installationID = null, CancellationToken cancellation = default)
    {
        // Одна прогонка PBKDF2 на адрес и на ключ шифрования разом. Считается до
        // запроса и не на потоке разметки: сто пятьдесят тысяч итераций — это
        // заметная доля секунды, и подвешивать на неё окно незачем.
        var bound = await Task.Run(() => new BoundActivationKey(key, installationID), cancellation)
            .ConfigureAwait(false);

        var channel = Provisioning.Current?.Updates;
        var url = channel?.ActivationUrl(bound.ObjectName);
        if (channel is null || url is null)
        {
            throw new PanelLinkException(PanelLinkFailure.KeyDidNotOpen);
        }

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(
            "Authorization", Provisioning.BasicHeader(channel.User, channel.Password));

        using CancellationTokenSource deadline = new(ChannelRequest.ActivationTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellation);

        HttpResponseMessage response;
        try
        {
            response = await ChannelRequest.Client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException
                                          && !cancellation.IsCancellationRequested)
        {
            throw ActivationChannelException.Of(error);
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                // 404 и 410 сводятся к одному ответу намеренно: «пакета нет» и
                // «пакет уже забрали» человеку означают одно и то же — нужен
                // новый ключ, — а различать их вслух значит подсказывать
                // подбирающему, какие адреса существуют.
                throw new PanelLinkException(PanelLinkFailure.KeyDidNotOpen);
            }

            var sealedPackage = await response.Content.ReadAsByteArrayAsync(linked.Token)
                .ConfigureAwait(false);

            return ActivationPackage.Open(sealedPackage, bound);
        }
    }
}

/// <summary>
/// Отказ, который не про ключ, а про связь.
/// </summary>
///
/// <remarks>
/// Отдельно от <see cref="PanelLinkException"/>, потому что разбирается иначе:
/// «нет сети» — это «попробуйте ещё раз», а «ключ не подошёл» — «попросите
/// новый». Свести их в один текст значило бы отправить человека за новым ключом
/// из-за отключённого Wi-Fi.
/// </remarks>
internal sealed class ActivationChannelException : Exception
{
    internal ActivationChannelException()
        : base(Text(string.Empty))
    {
    }

    internal ActivationChannelException(string message)
        : base(message)
    {
    }

    internal ActivationChannelException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Текст отказа: причина от системы внутри нашей формулировки.</summary>
    ///
    /// <remarks>
    /// Отдельным методом, а не своим конструктором: конструктор «причина плюс
    /// исходный отказ» повторил бы подпись обычного «сообщение плюс исходный
    /// отказ» знак в знак, и вызывающий не различал бы их вовсе.
    /// </remarks>
    internal static ActivationChannelException Of(Exception error)
        => new(Text(error.Message), error);

    private static string Text(string reason)
        => Resources.Strings.Format("ActivationNoChannel", reason);
}
