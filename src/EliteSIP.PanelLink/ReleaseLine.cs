namespace EliteSIP.PanelLink;

/// <summary>
/// Где на канале лежат выпуски этой ОС и как называется файл выпуска.
/// </summary>
///
/// <remarks>
/// <para>
/// Схема одна на все клиенты (<c>docs/RELEASES.md</c>): подписанный манифест
/// <c>current.json</c> и файл с версией в имени рядом с ним. Отличаются каталог
/// и расширение: Windows — <c>releases/EliteSIP-&lt;версия&gt;.exe</c>, Linux —
/// <c>releases/linux/EliteSIP-&lt;версия&gt;.deb</c>. Отдельный каталог, а не
/// второе имя рядом: манифесты двух ОС не должны пересекаться, как не
/// пересекаются appcast-файлы macOS.
/// </para>
/// <para>
/// Одно место на клиент, обновляльщик и релизный скрипт: прежде путь
/// <c>releases/current.json</c> и маска <c>EliteSIP-*.exe</c> были вписаны в
/// трёх местах, и Linux-клиент с любой из них пропущенной читал бы манифест
/// Windows.
/// </para>
/// </remarks>
public sealed record ReleaseLine
{
    private const string Prefix = "EliteSIP-";

    private ReleaseLine(string directory, string extension)
    {
        Directory = directory;
        Extension = extension;
    }

    public static ReleaseLine Windows { get; } = new("releases/", ".exe");

    public static ReleaseLine Linux { get; } = new("releases/linux/", ".deb");

    /// <summary>Линия ОС, на которой работает сборка.</summary>
    public static ReleaseLine Current { get; } = OperatingSystem.IsLinux() ? Linux : Windows;

    /// <summary>Каталог выпусков от корня канала, со слешем на конце.</summary>
    public string Directory { get; }

    /// <summary>Расширение файла выпуска, с точкой.</summary>
    public string Extension { get; }

    /// <summary>Манифест от корня канала.</summary>
    public string Manifest => Directory + "current.json";

    /// <summary>Маска для поиска скачанных выпусков в каталоге.</summary>
    public string FilePattern => Prefix + "*" + Extension;

    /// <summary>Имя файла выпуска: <c>EliteSIP-0.1.56.exe</c>.</summary>
    public string FileName(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return Prefix + version.ToString(3) + Extension;
    }

    /// <summary>Версия из имени файла этой линии; чужое имя — <c>null</c>.</summary>
    public Version? VersionOf(string path)
    {
        var name = Path.GetFileName(path);

        if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Version.TryParse(name[Prefix.Length..^Extension.Length], out var version) ? version : null;
    }

    /// <summary>
    /// Указывает ли манифест на файл этой линии.
    /// </summary>
    ///
    /// <remarks>
    /// <para>
    /// Проверяется только на Linux. Манифест Windows подписан ключом линии
    /// Windows, и Linux-клиент его не примет и так: ключи у линий свои. Эта
    /// проверка — вторая стена на случай, если ключи однажды сведут или
    /// перепутают в заводской настройке: Linux-машина не понесёт в <c>apt</c>
    /// установщик Windows, даже подписанный.
    /// </para>
    /// <para>
    /// У Windows-линии её нет намеренно: клиент Windows так не проверял, и
    /// раздел общего кода не должен менять его поведение.
    /// </para>
    /// </remarks>
    public bool Holds(ReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (this != Linux)
        {
            return true;
        }

        return manifest.Url.AbsolutePath.EndsWith(
            "/" + Directory + FileName(manifest.Version), StringComparison.Ordinal);
    }
}
