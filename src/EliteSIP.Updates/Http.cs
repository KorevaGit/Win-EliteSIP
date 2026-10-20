using System.Net;
using System.Net.Http;

namespace EliteSIP.Updater;

/// <summary>Общий клиент. Кэш запрещён — как и у приложения.</summary>
internal static class Http
{
    internal static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        // Четверть часа: выпуск весит полсотни мегабайт, и на плохом канале
        // конторы это минуты. У приложения тот же срок — см. `ChannelRequest`.
        Timeout = TimeSpan.FromMinutes(15),
        DefaultRequestHeaders =
        {
            CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true,
            },
        },
    };
}
