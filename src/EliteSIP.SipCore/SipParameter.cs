namespace EliteSIP.SipCore;

/// <summary>
/// Параметр URI, Via или заголовка: <c>transport=tls</c>, <c>lr</c>, <c>tag=x</c>.
///
/// Параметры везде хранятся списком, а не словарём, и это не небрежность:
/// порядок важен, потому что URI из входящего запроса иногда приходится
/// возвращать байт-в-байт (например <c>Refer-To</c> в <c>NOTIFY</c> при
/// переводе). Словарь этот порядок теряет.
///
/// Значение <see langword="null"/> означает флаг без значения (<c>;lr</c>,
/// <c>;rport</c> в запросе) — это не то же самое, что пустая строка: пустая
/// строка сериализуется как <c>;rport=</c> и меняет смысл.
/// </summary>
public readonly record struct SipParameter(string Name, string? Value = null)
{
    public override string ToString() => Value is null ? Name : $"{Name}={Value}";
}
