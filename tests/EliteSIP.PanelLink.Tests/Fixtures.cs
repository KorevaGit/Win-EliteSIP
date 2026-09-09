namespace EliteSIP.PanelLink.Tests;

/// <summary>
/// Образцы, собранные <b>настоящей панелью на Go</b>, а не подделанные здесь же.
/// </summary>
///
/// <remarks>
/// Это главная проверка пакета целиком. Всё остальное — вывод адреса, вывод
/// соли, число итераций, порядок nonce и метки, заголовок в дополнительных
/// данных, разбор конверта и проверка подписи — проверяется тем, что эти
/// образцы открываются. Собранный своими руками образец проверял бы только то,
/// что мы согласны сами с собой.
///
/// Байт в байт те же, что в тестах оригинала: расхождение порта с macOS-версией
/// обязано быть видно здесь, а не на машине сотрудника.
///
/// Перевыпускаются <c>go run ./cmd/fixtures</c> в <c>elitesupport</c>.
/// Разойдутся стороны — разойдётся здесь.
/// </remarks>
internal static class Fixture
{
    /// <summary>Машина, которой выдан пакет и адресованы помашинные объекты.</summary>
    internal const string InstallationID = "8f2c4a1b9d3e5f60";

    internal const string PresetID = "6D1F5A20-0000-4000-8000-000000000001";

    /// <summary>Ключ активации из образца — тот, что диктуют человеку.</summary>
    internal const string Key = "K7M2-9XQP-4TFB";

    /// <summary>Адрес пакета в канале, посчитанный панелью.</summary>
    internal const string ObjectName = "255a2e7e8e0e6e8260ab4e21f7f179bd";

    /// <summary>Открытый ключ линии предустановок.</summary>
    internal const string PublicKeyBase64 = "A6EHv/POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbg=";

    internal static PanelPublicKey PublicKey => PanelPublicKey.FromBase64(PublicKeyBase64);

    /// <summary>Запечатанный пакет активации.</summary>
    internal static byte[] SealedPackage => Convert.FromBase64String(
            "RVNJUEEydM0lPUtwahluFO2Z0Kj47Ubp6+h9a3KdaWBk9KiEkfk/fIwkcx3P4t87puESuB8xoqkNTw26FvrAcchFXZAj3urQUM3eadOMItiUgCcLoh3I8dmA"
            + "b6zG0mGpeCBAQMZItu6PhWGq7CFkPxeoPIdiRy7Ou0CrSas2lmYhgZyqeuVRU2JT3zJYXdg6aaD0jfTui+IiaQErzfEo7/X49eR5tx/EWnXBnuu5xpxWuPKT"
            + "JcOTJVAvez4dHSRrvslTlsdTA/M3lmFdyWSpJBUPFNqnkWAqsGEUcu/9VnAcW079Lh3AkSQDuLSeWtoYZsi/IVNUNJZWoyJrSMIOAgCYddsIQldmFn6bxKoE"
            + "qQdRMIPluG63CM5qM5m481uVze4J8BRsos1qphvWbNrpKnUCTWFvPzO9Ks95bSawGzWjc3acad4eTHrHTrDGux5F4DSlmABJOIDsFflDZlWDi5S7n4WnI/98"
            + "pXK9RcDn2Q8J/JADWqgjsLa+ph5JstKryvN28gET2DYMNe8SE/ZM6T6f8dZGBr4kEceKGaouj7rW5CDnDkIaVx0x06AK42vme7tssZ02wunPH9JJseBv0a7K"
            + "j2Es2nJ+sHVQ41SkU/asZimy6SYBSfa+FWM7aaD8+BfEBKSANpi5YGA2hBpReyLuutzP78yIZ91EfDFXuTsSorH95xbGf70r"
    );

    /// <summary>Помашинный доступ: подписанный конверт с административным паролем.</summary>
    internal static byte[] MachineAccessEnvelope => Convert.FromBase64String(
            "eyJwYXlsb2FkIjoiZXlKbWIzSnRZWFFpT2pFc0ltbHVjM1JoYkd4aGRHbHZibDlwWkNJNklqaG1NbU0wWVRGaU9XUXpaVFZtTmpBaUxDSndjbV"
            + "Z6WlhSZmFXUWlPaUkyUkRGR05VRXlNQzB3TURBd0xUUXdNREF0T0RBd01DMHdNREF3TURBd01EQXdNREVpTENKaFpHMXBibDl3WVhOemQyOXla"
            + "Q0k2SXRDLzBMRFJnTkMrMEx2UmpDM1F2OUdBMExYUXROR0QwWUhSZ3RDdzBMM1F2dEN5MExyUXVDSXNJbWx6YzNWbFpGOWhkQ0k2SWpJd01qWX"
            + "RNRGd0TWpWVU1USTZNREE2TURCYUluMD0iLCJzaWduYXR1cmUiOiJURDlRR0M2TG1HM21uanp1UHJKUDY5blpab0RaNGw1T1NEc29sdDVrejVx"
            + "Nm1aZkpsNk9sNjRDY05mL3FDRVRJRkRJSmtuNVFKNXBPTXFxQVlRVHZDZz09In0="
    );

    /// <summary>Отзыв: подписанный конверт, единственное, что сбрасывает машину.</summary>
    internal static byte[] RevocationEnvelope => Convert.FromBase64String(
            "eyJwYXlsb2FkIjoiZXlKbWIzSnRZWFFpT2pFc0ltbHVjM1JoYkd4aGRHbHZibDlwWkNJNklqaG1NbU0wWVRGaU9XUXpaVFZtTmpBaUxDSnlaWF"
            + "p2YTJWa1gyRjBJam9pTWpBeU5pMHdPQzB5TlZReE1qb3dNRG93TUZvaWZRPT0iLCJzaWduYXR1cmUiOiJma3lzMzNseG00UU52eURkMHJpNGp3"
            + "Y3hXZWJLNjZIV04vbmY2b0Jhb1k0Z3AwUmhyaXpWSytqTXJWWjhmN3J1QVRhdUQ2MkszWjczUm1WUkxCLzhEQT09In0="
    );

    /// <summary>Файл предустановок, подписанный панелью.</summary>
    internal static byte[] SignedBundle => Convert.FromBase64String(
            "eyJwYXlsb2FkIjoiZXlKbWIzSnRZWFFpT2pFc0ltZGxibVZ5WVhSbFpGOWhkQ0k2SWpJd01qWXRNRGd0TWpSVU1UVTZNekE2TURCYUlp"
            + "d2ljSEpsYzJWMGN5STZXM3NpYVdRaU9pSTJSREZHTlVFeU1DMHdNREF3TFRRd01EQXRPREF3TUMwd01EQXdNREF3TURBd01ERWlMQ0p1"
            + "WVcxbElqb2kwSnpRdGRDOTBMWFF0TkMyMExYUmdDSXNJbkpsZG1semFXOXVJam94TWl3aWMyTm9aVzFoWDNabGNuTnBiMjRpT2pJc0lt"
            + "WnBaV3hrY3lJNmV5SnphWFJsUVdSa2NtVnpjMlZ6SWpwN0ltOW1abWxqWlNJNklqRTVNaTR4TmpndU1TNHlJaXdpY21WdGIzUmxJam9p"
            + "WTNKdExtVnNhWFJsYzI5amFHa3VZMjl0SW4xOWZWMTkiLCJzaWduYXR1cmUiOiJ4STBLdjh0NDJBUUlURndBYm8wZ2VuL2RUdUU5c0Fl"
            + "VVk0SENEODBwSjhtS0JuNm5XejBMblB2WHVMMmRLWWJ4cG0wdGZFdmRSRk0wbEY2dC83U3JDZz09In0="
    );
}
