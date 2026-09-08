namespace EliteSIP.AdminAccess;

/// <summary>Отчего отказано в административном доступе.</summary>
public enum AdminAccessFailure
{
    /// <summary>Пароль не подошёл.</summary>
    WrongPassword,

    /// <summary>Пароль пуст. Пустой пароль — это отсутствие пароля, а не пароль.</summary>
    EmptyPassword,

    /// <summary>
    /// Учётные данные в файле настроек испорчены: обрезаны, переписаны руками
    /// или сохранены сборкой, которая писала их иначе.
    /// </summary>
    MalformedCredential,

    /// <summary>
    /// Вывод ключа не удался. Практически недостижимо; существует, чтобы не
    /// превращать отказ в молчаливый нулевой ключ.
    /// </summary>
    DerivationFailed,
}

/// <summary>
/// Отказ административного доступа.
///
/// Исключением, а не возвращаемым значением: в оригинале это был
/// <c>enum AdminAccessError: Error</c>, который бросали через <c>throws</c>, и
/// у всех вызывающих он приходит в один и тот же <c>catch</c> — окно с текстом.
/// Причина при этом остаётся кодом (<see cref="Failure"/>), а не разбором
/// строки: тексты одинаково правильные и одинаково меняющиеся.
///
/// Тексты намеренно не уточняют, что именно не сошлось: «пароль неверен» и
/// «данные испорчены» не должны различаться подробностью для того, кто
/// подбирает.
///
/// Строки пока русские литералы — ресурсы ru/en появятся на W8, как и у
/// остальных перенесённых пакетов.
/// </summary>
public sealed class AdminAccessException : Exception
{
    public AdminAccessException(AdminAccessFailure failure)
        : base(TextOf(failure))
    {
        Failure = failure;
    }

    public AdminAccessException(AdminAccessFailure failure, Exception? innerException)
        : base(TextOf(failure), innerException)
    {
        Failure = failure;
    }

    /// <summary>Обязателен по правилам анализатора; в продукте не применяется.</summary>
    public AdminAccessException()
        : this(AdminAccessFailure.WrongPassword)
    {
    }

    /// <summary>Обязателен по правилам анализатора; в продукте не применяется.</summary>
    public AdminAccessException(string message)
        : base(message)
    {
        Failure = AdminAccessFailure.WrongPassword;
    }

    /// <summary>Обязателен по правилам анализатора; в продукте не применяется.</summary>
    public AdminAccessException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Failure = AdminAccessFailure.WrongPassword;
    }

    public AdminAccessFailure Failure { get; }

    private static string TextOf(AdminAccessFailure failure) => failure switch
    {
        AdminAccessFailure.WrongPassword => "Неверный пароль.",
        AdminAccessFailure.EmptyPassword => "Пароль не может быть пустым.",
        AdminAccessFailure.MalformedCredential
            => "Данные административного доступа испорчены. Задайте пароль заново.",
        AdminAccessFailure.DerivationFailed => "Не удалось проверить пароль.",
        _ => "Не удалось проверить пароль.",
    };
}
