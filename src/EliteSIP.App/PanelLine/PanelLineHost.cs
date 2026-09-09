using System.IO;
using System.Windows.Threading;
using EliteSIP.AdminAccess;
using EliteSIP.App.Settings;
using EliteSIP.PanelLink;

namespace EliteSIP.App.PanelLine;

/// <summary>
/// Линия панели целиком: два будильника, применение приехавшего и сброс по
/// отзыву.
/// </summary>
///
/// <remarks>
/// Одним местом, а не тремя, потому что склеивать их всё равно кому-то нужно, и
/// лучше пусть это будет свой класс, чем композиция приложения: там уже полсотни
/// строк, и линия панели растворилась бы в них.
///
/// В оригинале ту же роль играло расширение модели приложения
/// (<c>AppModel+Machine</c>): та же склейка, только висящая на большом типе.
/// </remarks>
internal sealed class PanelLineHost : IDisposable
{
    private readonly AppSettings _settings;
    private readonly AdminAccessState _access;
    private readonly Action<string> _log;
    private readonly Func<bool> _isBlocked;

    private readonly PresetService _presets;
    private readonly MachineService _machine;

    private readonly DispatcherTimer _presetTimer;
    private readonly DispatcherTimer _revocationTimer;

    /// <param name="isBlocked">
    /// идёт разговор или открыто «Управление». Применение ждёт обоих —
    /// см. <see cref="PresetService"/>.
    /// </param>
    /// <param name="reset">
    /// чем сбрасывать машину. Замыкание, а не вызов отсюда: чистка закрывает
    /// окна и перезапускает приложение, а это дело композиции, не линии.
    /// </param>
    internal PanelLineHost(
        AppSettings settings,
        AdminAccessState access,
        Func<bool> isBlocked,
        Action reset,
        Action<string> log)
    {
        _settings = settings;
        _access = access;
        _isBlocked = isBlocked;
        _log = log;

        _presets = new PresetService(() => _settings, ApplyPreset, isBlocked, log);
        _presets.NoteContact = () => _settings.Panel.LastContactAt = DateTimeOffset.UtcNow;

        _machine = new MachineService(
            () => _settings,
            ApplyMachineAccess,
            revocation => ResetByRevocation(revocation, reset),
            log);

        // Такт предустановок — два часа, отзыва — пятнадцать минут. Разные
        // сроки, потому что отзыв срабатывает ровно с задержкой опроса: на
        // двухчасовом такте уволенный работал бы ещё два часа после нажатия
        // «отозвать».
        _presetTimer = new DispatcherTimer { Interval = PresetService.Interval };
        _presetTimer.Tick += async (_, _) => await CheckAsync().ConfigureAwait(true);

        _revocationTimer = new DispatcherTimer { Interval = MachineService.RevocationInterval };
        _revocationTimer.Tick += async (_, _) => await _machine.CheckRevocationAsync().ConfigureAwait(true);
    }

    /// <summary>Ответ линии кнопке «Проверить настройки сейчас».</summary>
    internal Action<bool, string?>? Report
    {
        get => _presets.Report;
        set => _presets.Report = value;
    }

    /// <summary>Заводит оба будильника и спрашивает канал сразу.</summary>
    ///
    /// <remarks>
    /// Сразу — потому что машина могла простоять выключенной неделю: ждать двух
    /// часов после включения значит работать эту неделю плюс два часа по старым
    /// настройкам, а отзыв — пятнадцать минут сверх того.
    /// </remarks>
    internal void Start()
    {
        _presetTimer.Start();
        _revocationTimer.Start();

        _ = CheckAsync();
        _ = _machine.CheckRevocationAsync();
    }

    /// <summary>Спросить канал прямо сейчас: предустановки и свой доступ.</summary>
    internal async Task CheckAsync()
    {
        await _presets.CheckAsync().ConfigureAwait(true);
        await _machine.CheckAccessAsync().ConfigureAwait(true);
    }

    /// <summary>Помеха ушла — доложить отложенное.</summary>
    internal void HostBecameIdle() => _presets.HostBecameIdle();

    public void Dispose()
    {
        _presetTimer.Stop();
        _revocationTimer.Stop();
    }

    /// <summary>
    /// Применяет приехавшую ревизию.
    /// </summary>
    ///
    /// <remarks>
    /// Порядок тот же, что и при активации: сперва управляемые поля, потом
    /// память о панели. Правило «одна дорога на оба пути» из оригинала здесь
    /// держится тем, что применение пакета зовёт то же самое наложение.
    /// </remarks>
    private void ApplyPreset(PresetBundle.Entry entry)
    {
        var wasResync = _settings.Panel.WantsResync;

        _settings.Apply(ManagedFields.Parse(entry.Fields));

        _settings.Panel.PresetName = entry.Name;
        _settings.Panel.AppliedRevision = entry.Revision;
        _settings.Panel.AppliedAt = DateTimeOffset.UtcNow;

        // Просьба выполнена — снимается здесь и только здесь. Оставленный
        // признак означал бы, что машина переприменяет предустановку каждые два
        // часа, затирая ей же разрешённое локальное.
        _settings.Panel.WantsResync = false;

        _log(wasResync
            ? $"предустановка переприменена по просьбе машины: «{entry.Name}», ревизия {entry.Revision}"
            : $"предустановка применена: «{entry.Name}», ревизия {entry.Revision}");
    }

    /// <summary>
    /// Применяет помашинный доступ, приехавший с канала.
    /// </summary>
    ///
    /// <remarks>
    /// Административный пароль стал полем предустановки: у техподдержки своя
    /// предустановка со своим паролем. В общий файл предустановок он не едет —
    /// файл один на контору, и любой оператор прочитал бы там чужой пароль, —
    /// поэтому приезжает отдельным подписанным объектом.
    ///
    /// <b>Пароль ставится, только если он изменился.</b> Иначе каждый заход на
    /// канал перевыводил бы ключ из пароля: PBKDF2 со ста пятьюдесятью тысячами
    /// итераций раз в два часа не нужен ни для чего.
    /// </remarks>
    private void ApplyMachineAccess(MachineAccess access)
    {
        if (access.AdminPassword.Length == 0)
        {
            return;
        }

        if (_settings.Admin.ToCredential()?.Matches(access.AdminPassword) is true)
        {
            return;
        }

        try
        {
            // Не через `SetPassword`: тот требует открытого режима — иначе
            // кнопка «сменить пароль» была бы обходом пароля. Здесь пароль
            // меняет не человек за столом, а панель, и требовать от неё войти
            // в «Управление» не с чем.
            var credential = AdminCredential.Create(access.AdminPassword);
            _settings.Admin.From(credential);
            _access.Restore(credential);

            _log("административный пароль приехал с панели");
        }
        catch (AdminAccessException error)
        {
            // Пароль не лёг — машина всё равно поднята и звонит. Ронять из-за
            // этого рабочее место незачем, но и молчать нельзя: «Управление» на
            // ней откроется прежним паролем, и знать об этом надо.
            _log($"административный пароль с панели не применён: {error.Message}");
        }
    }

    /// <summary>
    /// Сбрасывает машину по подписанному отзыву.
    /// </summary>
    ///
    /// <remarks>
    /// <b>Чистка полная: не остаётся ничего.</b> Уносится всё — учётка, адреса,
    /// клавиши, очереди, административный пароль, история звонков и журнал.
    /// Машина возвращается в состояние сразу после установки и требует мастер
    /// заново.
    ///
    /// Половинчатая чистка была бы хуже отсутствия отзыва: машина, у которой
    /// стёрли только пароль, не регистрируется — и при этом несёт всю карту
    /// телефонии конторы. Для «сотрудник уволился, ноутбук у него» это половина
    /// защиты.
    ///
    /// Цена полной чистки названа прямо: после неё разбирать ошибочный отзыв не
    /// по чему — журнал и история уходят вместе со всем остальным.
    ///
    /// В разговоре не сбрасываем: сброс снимает регистрацию, то есть кладёт
    /// трубку за оператора. Ждать безопасно — отзыв лежит в канале и
    /// спрашивается каждые пятнадцать минут.
    /// </remarks>
    private void ResetByRevocation(Revocation revocation, Action reset)
    {
        if (_isBlocked())
        {
            _log($"рабочее место отозвано, сброс ждёт конца разговора: машина {revocation.InstallationID}");
            return;
        }

        // Строка пишется до сброса, хотя журнал он и стирает: в те несколько
        // мгновений, что она живёт, её видит открытая «Диагностика».
        _log($"рабочее место отозвано панелью — полная чистка: машина {revocation.InstallationID}");

        reset();
    }
}

/// <summary>Полная чистка машины: то, что оставляет её как сразу после установки.</summary>
internal static class MachineReset
{
    /// <summary>
    /// Стирает всё, что машина накопила.
    /// </summary>
    ///
    /// <remarks>
    /// Файлами, а не обнулением объектов в памяти: история и журнал живут в
    /// своих файлах, и обход по настройкам их не тронул бы. Что не стёрлось —
    /// пишется в возвращаемый список, но чистка от этого не останавливается:
    /// занятый журнал не повод оставить на диске пароль SIP.
    /// </remarks>
    internal static IReadOnlyList<string> Wipe()
    {
        var directory = Path.GetDirectoryName(AppSettings.DefaultPath)!;
        var failures = new List<string>();

        foreach (var name in new[]
        {
            Path.GetFileName(AppSettings.DefaultPath),
            "history.db",
            "history.db-wal",
            "history.db-shm",
            "activation-draft.dat",
            "elitesip.log",
            "crash.log",
        })
        {
            try
            {
                File.Delete(Path.Combine(directory, name));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                failures.Add(name);
            }
        }

        return failures;
    }
}
