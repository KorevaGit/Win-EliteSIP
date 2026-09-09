using EliteSIP.PanelLink.Resources;

namespace EliteSIP.PanelLink;

/// <summary>Отчего не разобралось то, что пришло от панели.</summary>
///
/// <remarks>
/// Список короткий намеренно. Он не описывает, что именно пошло не так внутри
/// криптографии, — он описывает то, что человеку осмысленно показать.
/// </remarks>
public enum PanelLinkFailure
{
    /// <summary>
    /// Это не ключ активации: не тот состав или не та длина.
    ///
    /// Отличается от <see cref="KeyDidNotOpen"/> намеренно: здесь человек
    /// ошибся при вводе и может исправиться сам, а там ключ вводом уже не
    /// спасти.
    /// </summary>
    MalformedKey,

    /// <summary>
    /// Ключ не подошёл или пакет уже забрали.
    ///
    /// <b>Неверный ключ и испорченный пакет неотличимы по ответу, и это
    /// решение, а не небрежность.</b> То же, что у пароля в
    /// <c>AdminAccess</c>: подбирающему незачем знать, ошибся он ключом или
    /// наткнулся на битый файл.
    /// </summary>
    KeyDidNotOpen,

    /// <summary>
    /// Пакет собран более новой версией панели.
    ///
    /// Отдельным случаем, потому что иначе разбор уйдёт не туда: человек будет
    /// искать опечатку в ключе, которого не набирал, вместо того чтобы
    /// обновить приложение.
    /// </summary>
    PackageTooNew,

    /// <summary>Файл предустановок не прошёл проверку подписи.</summary>
    SignatureDidNotMatch,

    /// <summary>Файл предустановок собран более новой версией.</summary>
    BundleTooNew,

    /// <summary>Файл предустановок не разобрался.</summary>
    MalformedBundle,
}

/// <summary>
/// Отказ при разборе того, что пришло от панели.
///
/// Исключением, а не возвращаемым значением, — по тем же соображениям, что и
/// <c>AdminAccessException</c>: в оригинале это был <c>enum PanelLinkError:
/// Error</c>, который бросали через <c>throws</c>. Причина остаётся кодом
/// (<see cref="Failure"/>), а не разбором строки: мастер первого запуска
/// разводит <see cref="PanelLinkFailure.MalformedKey"/> и
/// <see cref="PanelLinkFailure.KeyDidNotOpen"/> на разные шаги.
/// </summary>
public sealed class PanelLinkException : Exception
{
    public PanelLinkException(PanelLinkFailure failure)
        : base(TextOf(failure))
    {
        Failure = failure;
    }

    public PanelLinkException(PanelLinkFailure failure, Exception? innerException)
        : base(TextOf(failure), innerException)
    {
        Failure = failure;
    }

    /// <summary>Обязателен по правилам анализатора; в продукте не применяется.</summary>
    public PanelLinkException()
        : this(PanelLinkFailure.MalformedBundle)
    {
    }

    /// <summary>Обязателен по правилам анализатора; в продукте не применяется.</summary>
    public PanelLinkException(string message)
        : base(message)
    {
        Failure = PanelLinkFailure.MalformedBundle;
    }

    /// <summary>Обязателен по правилам анализатора; в продукте не применяется.</summary>
    public PanelLinkException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Failure = PanelLinkFailure.MalformedBundle;
    }

    public PanelLinkFailure Failure { get; }

    private static string TextOf(PanelLinkFailure failure) => failure switch
    {
        PanelLinkFailure.MalformedKey => PackageStrings.Get("RefusalMalformedKey"),
        PanelLinkFailure.KeyDidNotOpen => PackageStrings.Get("RefusalKeyDidNotOpen"),
        PanelLinkFailure.PackageTooNew => PackageStrings.Get("RefusalPackageTooNew"),
        PanelLinkFailure.SignatureDidNotMatch => PackageStrings.Get("RefusalSignatureDidNotMatch"),
        PanelLinkFailure.BundleTooNew => PackageStrings.Get("RefusalBundleTooNew"),
        PanelLinkFailure.MalformedBundle => PackageStrings.Get("RefusalMalformedBundle"),
        _ => PackageStrings.Get("RefusalMalformedBundle"),
    };
}
