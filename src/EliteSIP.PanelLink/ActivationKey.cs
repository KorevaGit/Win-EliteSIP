using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EliteSIP.PanelLink;

/// <summary>
/// Ключ активации — то, что сотрудник вводит в мастере.
/// </summary>
///
/// <remarks>
/// Ключ делает три вещи сразу, и это решение панели, а не наше удобство:
/// называет пакет в канале раздачи, отпирает его и опознаёт активацию в панели.
/// Отсюда главное следствие для приложения: <b>ключ нигде не сохраняется.</b>
/// Он одноразовый, и второй раз пакет не отдадут.
///
/// Формат и выводы адресов — elitesupport/docs/CONTRACT.md.
/// </remarks>
public readonly struct ActivationKey : IEquatable<ActivationKey>
{
    /// <summary>
    /// Алфавит Crockford Base32.
    ///
    /// Из него убраны <c>I</c>, <c>L</c>, <c>O</c> и <c>U</c>: первые три
    /// путаются с единицей и нулём на слух и в шрифтах, последняя выпала, чтобы
    /// из ключа случайно не складывалось слов.
    /// </summary>
    internal const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Длина ключа в знаках.</summary>
    public const int Length = 12;

    private ActivationKey(string canonical) => Canonical = canonical;

    /// <summary>Канонический вид: двенадцать знаков алфавита, без разделителей.</summary>
    public string Canonical { get; }

    /// <summary>
    /// Разбирает то, что ввёл человек.
    /// </summary>
    ///
    /// <remarks>
    /// Разбор нарочно терпимый. Ключ диктуют по телефону и вставляют из
    /// мессенджера вместе с пробелами и переносами, поэтому:
    ///
    /// <list type="bullet">
    ///   <item><description>регистр не важен;</description></item>
    ///   <item><description>выбрасывается всё, что не буква и не цифра;</description></item>
    ///   <item><description><c>O</c> приводится к нулю, <c>I</c> и <c>L</c> — к единице.</description></item>
    /// </list>
    ///
    /// Последнее не любезность, а необходимость: этих букв в алфавите нет
    /// вовсе, и прочитавший ноль как «о» иначе получал бы отказ, не понимая
    /// почему.
    ///
    /// <b>Разделители не перечисляются списком, а определяются от обратного.</b>
    /// В оригинале это правило появилось после ошибки: перечисленные поимённо,
    /// они спотыкались на паре CR LF — в Swift это один <c>Character</c>, а не
    /// два, — и ключ, скопированный из мессенджера на Windows, отвергался бы как
    /// непохожий на ключ. В C# такой ловушки нет, знаки перебираются по одному,
    /// но правило сохранено: списком его пришлось бы доводить ещё и на
    /// неразрывном пробеле, длинном тире и всём прочем, что вставляется вместе
    /// с текстом.
    ///
    /// С 25 августа 2026 то же правило действует и в панели: до него панель
    /// перечисляла разделители списком и была строже приложения — ключ, который
    /// приложение принимало, поиск по ключу отвергал как «не ключ».
    /// </remarks>
    ///
    /// <exception cref="PanelLinkException">
    /// <see cref="PanelLinkFailure.MalformedKey"/>, если знаков не двенадцать
    /// или среди них есть не входящий в алфавит.
    /// </exception>
    public static ActivationKey Parse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var digits = new StringBuilder(Length);

        foreach (var character in input.ToUpperInvariant())
        {
            if (!char.IsLetter(character) && !char.IsDigit(character))
            {
                continue;
            }

            switch (character)
            {
                case 'O':
                    digits.Append('0');
                    break;

                case 'I':
                case 'L':
                    digits.Append('1');
                    break;

                default:
                    if (!Alphabet.Contains(character, StringComparison.Ordinal))
                    {
                        throw new PanelLinkException(PanelLinkFailure.MalformedKey);
                    }

                    digits.Append(character);
                    break;
            }
        }

        if (digits.Length != Length)
        {
            throw new PanelLinkException(PanelLinkFailure.MalformedKey);
        }

        return new ActivationKey(digits.ToString());
    }

    /// <summary>Показать так, как показывает панель: группами по четыре.</summary>
    public string Grouped
    {
        get
        {
            var groups = new List<string>();
            for (var offset = 0; offset < Canonical.Length; offset += 4)
            {
                groups.Add(Canonical.Substring(offset, Math.Min(4, Canonical.Length - offset)));
            }

            return string.Join('-', groups);
        }
    }

    public bool Equals(ActivationKey other)
        => string.Equals(Canonical, other.Canonical, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ActivationKey other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Canonical ?? string.Empty);

    public override string ToString() => Grouped;

    public static bool operator ==(ActivationKey left, ActivationKey right) => left.Equals(right);

    public static bool operator !=(ActivationKey left, ActivationKey right) => !left.Equals(right);
}

/// <summary>
/// Ключ вместе с выведенным из него материалом.
/// </summary>
///
/// <remarks>
/// Тип существует затем, чтобы прогонка была ровно одна. Имя объекта и ключ
/// шифрования — разные куски одного вывода PBKDF2, и считать их по отдельности
/// значило бы заплатить второй раз за сто пятьдесят тысяч итераций, пока
/// человек ждёт у экрана. Поэтому же это не вычисляемые свойства ключа: такая
/// работа не должна прятаться за точкой.
/// </remarks>
public sealed class BoundActivationKey
{
    /// <summary>
    /// Число итераций PBKDF2.
    ///
    /// Взято у панели, а панель взяла его у <c>AdminAccess.KeyDerivation</c>.
    /// Argon2id здесь был бы уместнее, но в оригинале его не было ни в
    /// CryptoKit, ни в CommonCrypto на Catalina; во встроенной криптографии .NET
    /// его нет и подавно, а тянуть ради него вторую зависимость в пакет с одной
    /// нельзя. Разбор в elitesupport/docs/DECISIONS.md.
    /// </summary>
    internal const int Iterations = 150_000;

    internal const int NameLength = 16;
    internal const int KeyLength = 32;

    /// <summary>
    /// Считает материал ключа.
    /// </summary>
    ///
    /// <remarks>
    /// <code>
    /// соль  = SHA-256("elitesip.activation.salt.v2\0" + ключ + "\0" + машина)[:16]
    /// вывод = PBKDF2-HMAC-SHA256(ключ, соль, 150 000, 48)
    /// имя объекта = hex(вывод[0..16])
    /// ключ AES    = вывод[16..48]
    /// </code>
    ///
    /// <b>Соль выводится из ключа, а не берётся случайной.</b> Обычно так
    /// нельзя; здесь можно, потому что ключ случаен и живёт двое суток —
    /// повторов, ради которых соль и случайна, не бывает. Зато машине не нужно
    /// знать ничего, кроме ключа: ни соли рядом с шифротекстом, ни параметров в
    /// заголовке.
    ///
    /// <b>Имя объекта растянуто вместе с ключом шифрования.</b> Раньше оно
    /// считалось голым SHA-256 от ключа — а в ключе шестьдесят бит, и утёкшая
    /// база панели давала готовый образ для перебора.
    /// </remarks>
    ///
    /// <param name="key">ключ, который ввёл человек.</param>
    /// <param name="installationID">
    /// машина, к которой привязан ключ. <c>null</c> у ключа активации: машины
    /// ещё нет. У ключа перепрошивки — идентификатор этой машины, и тогда чужой
    /// ключ считает другой адрес и не находит по нему ничего. Проверять привязку
    /// внутри пакета было нельзя: Worker столбит пакет в момент скачивания, и
    /// перепутавший свои же два компьютера сжигал бы ключ до всякой проверки.
    /// </param>
    public BoundActivationKey(ActivationKey key, string? installationID = null)
    {
        var binding = installationID ?? string.Empty;
        var salt = SHA256.HashData(
                Encoding.UTF8.GetBytes($"elitesip.activation.salt.v2\0{key.Canonical}\0{binding}"))
            .AsSpan(0, 16)
            .ToArray();

        byte[] material;
        try
        {
            material = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(key.Canonical),
                salt,
                Iterations,
                HashAlgorithmName.SHA256,
                NameLength + KeyLength);
        }
        catch (CryptographicException error)
        {
            // Отказ криптопровайдера не превращается в молчаливый нулевой ключ —
            // то же правило, что и в AdminAccess.KeyDerivation.
            throw new PanelLinkException(PanelLinkFailure.KeyDidNotOpen, error);
        }

        ObjectName = Convert.ToHexString(material.AsSpan(0, NameLength))
            .ToLower(CultureInfo.InvariantCulture);
        CipherKey = material.AsSpan(NameLength, KeyLength).ToArray();
    }

    /// <summary>
    /// Имя пакета в канале раздачи — та самая шестнадцатеричная строка.
    ///
    /// Приставка <c>activations/</c> сюда не входит: она принадлежит раскладке
    /// бакета и приезжает внутри адреса канала.
    /// </summary>
    public string ObjectName { get; }

    /// <summary>Ключ AES-GCM. Наружу не отдаётся.</summary>
    internal byte[] CipherKey { get; }
}
