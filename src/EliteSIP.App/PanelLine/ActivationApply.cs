using EliteSIP.AdminAccess;
using EliteSIP.App.Settings;
using EliteSIP.PanelLink;

namespace EliteSIP.App.PanelLine;

/// <summary>
/// Применение пакета активации к машине.
/// </summary>
///
/// <remarks>
/// <b>Одна дорога на оба пути.</b> Пакет приезжает дважды за жизнь машины — при
/// первой активации и при перепрошивке, — и правило «номер, потом управляемые
/// поля, потом память о панели» должно быть одно на оба, а не два похожих. Два
/// похожих разошлись бы на первой же новой настройке, и разницу нашли бы на
/// живой машине.
/// </remarks>
internal static class ActivationApply
{
    /// <summary>
    /// Кладёт пакет в настройки.
    /// </summary>
    ///
    /// <remarks>
    /// Порядок значим целиком: сперва учётка, потом настройки конторы, пароли
    /// последними. Ревизия ставится после наложения полей — до него она означала
    /// бы «применено» там, где ещё ничего не применялось.
    ///
    /// Идентификатор машины при перепрошивке тот же самый: панель выпускает ключ
    /// на выбранную машину, и он же входит в вывод адреса пакета. Значит
    /// присваивание ничего не меняет, и это правильно — смена идентификатора
    /// разорвала бы историю отметок надвое.
    /// </remarks>
    ///
    /// <param name="access">
    /// помашинный доступ, если он приехал тем же заходом. <c>null</c> — машина
    /// остаётся с прежним административным паролем, и новый догонит её первым же
    /// тактом линии.
    /// </param>
    internal static void Apply(
        this AppSettings settings,
        ActivationPackage package,
        MachineAccess? access,
        AdminAccessState adminAccess)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(adminAccess);

        settings.Account.Username = package.Number;
        settings.Account.DisplayName = package.Employee;
        settings.Credentials.SetPassword(package.SipPassword);

        settings.Panel.InstallationID = package.InstallationID;
        settings.Panel.SetChannelKey(package.ChannelKey);
        settings.Panel.PresetID = package.Preset.ID;
        settings.Panel.PresetName = package.Preset.Name;
        settings.Panel.Mode = PanelMode.Managed;

        settings.Apply(ManagedFields.Parse(package.Preset.Settings));

        settings.Panel.AppliedRevision = package.Preset.Revision;
        settings.Panel.AppliedAt = DateTimeOffset.UtcNow;

        // Пара адресов — настройка машины, а регистрируется учётка по своему
        // домену, и одно из другого само не следует. Без этой строки на свежей
        // машине номер есть, пароль есть, а регистрироваться некуда.
        //
        // Площадку выбирает сама машина; «Офис» здесь не догадка о том, где
        // сидит человек, а умолчание для той, которую только заводят.
        var wanted = settings.Account.Site is WorkplaceSite.Remote
            ? settings.Pbx.RemoteAddress
            : settings.Pbx.OfficeAddress;

        if (wanted.Length > 0)
        {
            settings.Account.Domain = wanted;
        }

        if (access is { AdminPassword.Length: > 0 })
        {
            var credential = AdminCredential.Create(access.AdminPassword);
            settings.Admin.From(credential);
            adminAccess.Restore(credential);
        }
    }
}
