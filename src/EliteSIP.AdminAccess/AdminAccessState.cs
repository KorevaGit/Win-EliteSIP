namespace EliteSIP.AdminAccess;

// `AdminManagement` в оригинале убран 25 августа 2026.
//
// Перечисление было о том, откуда пришли настройки: «локальный режим» против
// «настройки из файла конфигурации». Файла конфигурации больше нет, и второе
// значение стало недостижимым — а с одним оставшимся тип начал врать: машина,
// которой управляет панель, показывала бы «Локальный режим».
//
// Управляемость машины живёт там, где ей и место, — рядом с предустановкой,
// ревизией и ключом канала панели. Этот проект о панели не знает и знать не
// должен: он про пароль.

/// <summary>
/// Административный доступ целиком: что помним между запусками и открыт ли
/// режим прямо сейчас.
///
/// Открытость сессии живёт здесь же, но не сохраняется — она уходит вместе с
/// окном настроек. Держать её в файле значило бы однажды оставить рабочее место
/// распахнутым после перезапуска, и узнать об этом было бы неоткуда.
///
/// Классом, а не структурой: в оригинале это была структура с
/// <c>mutating func</c>, и владелец хранил её у себя одним значением. В C#
/// структура с изменяющими методами — источник тихой потери изменений: копия,
/// сделанная захватом в лямбду или передачей в метод, откроет режим у себя, а не
/// у владельца. Ссылочный тип здесь честнее описывает то, что и так было одним
/// экземпляром на приложение.
/// </summary>
public sealed class AdminAccessState
{
    /// <summary>
    /// Новое состояние — всегда закрытое.
    ///
    /// <see cref="IsUnlocked"/> намеренно нельзя задать снаружи: иначе «открыть
    /// режим» сводилось бы к присваиванию, и проверка пароля превращалась бы в
    /// формальность, которую легко обойти по невнимательности. Открыть режим
    /// можно только через <see cref="Unlock"/>, то есть только предъявив пароль.
    /// </summary>
    public AdminAccessState(AdminCredential? credential = null)
    {
        Credential = credential;
        IsUnlocked = false;
    }

    /// <summary>Проверочное значение пароля. <c>null</c> — пароль не задан.</summary>
    public AdminCredential? Credential { get; private set; }

    /// <summary>Открыт ли режим прямо сейчас.</summary>
    public bool IsUnlocked { get; private set; }

    /// <summary>Задан ли пароль.</summary>
    public bool IsProtected => Credential is not null;

    /// <summary>
    /// Видна ли закрытая часть настроек.
    ///
    /// Пока пароль не задан — видна всем. Так решено: свежая машина
    /// настраивается с аккаунта, и начинать эту настройку с придумывания пароля
    /// значит, что первый же установщик пароль и не задаст.
    /// </summary>
    public bool AllowsAdministration => Credential is null || IsUnlocked;

    /// <summary>Вход по паролю.</summary>
    public bool Unlock(string password)
    {
        if (Credential is null)
        {
            // Пароля нет — открывать нечего, но и отказывать не за что.
            IsUnlocked = true;
            return true;
        }

        if (!Credential.Matches(password))
        {
            throw new AdminAccessException(AdminAccessFailure.WrongPassword);
        }

        IsUnlocked = true;
        return true;
    }

    // Входа по коду восстановления здесь нет.
    //
    // Код лежал открытым текстом в бандле каждого приложения, то есть
    // «восстановление» работало у всякого, кто вскрыл `.app`. Теперь актуальный
    // пароль показывает панель, потому что оттуда он и приезжает.

    /// <summary>Выход из режима.</summary>
    public void Lock() => IsUnlocked = false;

    /// <summary>
    /// Задать или сменить пароль.
    ///
    /// Смена требует открытого режима — иначе кнопка «сменить пароль» была бы
    /// обходом пароля. Первая установка открытого режима не требует: пока пароля
    /// нет, <see cref="AllowsAdministration"/> и так истинно.
    /// </summary>
    public void SetPassword(string password)
    {
        if (!AllowsAdministration)
        {
            throw new AdminAccessException(AdminAccessFailure.WrongPassword);
        }

        Credential = AdminCredential.Create(password);
        IsUnlocked = true;
    }

    /// <summary>Снять пароль совсем.</summary>
    public void RemovePassword()
    {
        if (!AllowsAdministration)
        {
            throw new AdminAccessException(AdminAccessFailure.WrongPassword);
        }

        Credential = null;
        IsUnlocked = true;
    }

    /// <summary>
    /// Учётные данные из файла настроек.
    ///
    /// Отдельно от конструктора: при загрузке режим всегда закрыт, чем бы ни был
    /// занят предыдущий запуск.
    /// </summary>
    public void Restore(AdminCredential? credential)
    {
        Credential = credential;
        IsUnlocked = false;
    }
}
