using System.Reflection;

namespace EliteSIP.Audio.Tests;

/// <summary>
/// Строка журнала про обработку Windows на микрофоне.
/// </summary>
///
/// <remarks>
/// Сам опрос <c>IAudioEffectsManager</c> без звуковой карты не проверить;
/// проверяется разбор списка и то, на чём держится доступ к нему в NAudio.
/// </remarks>
public sealed class CaptureEffectsTests
{
    [Fact]
    public void Поле_клиента_в_NAudio_на_месте()
    {
        // Список эффектов достаётся через закрытое поле NAudio. Обновление
        // библиотеки, переименовавшее его, должно уронить этот тест, а не
        // молча превратить строку журнала в «не узнать».
        FieldInfo? field = typeof(NAudio.CoreAudioApi.AudioClient).GetField(
            CaptureEffects.ClientField,
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        Assert.Equal("IAudioClient", field.FieldType.Name);
    }

    [Fact]
    public void Windows_10_так_и_называется()
    {
        string text = CaptureEffects.Summarize(null, null, ownEcho: true, ownNoise: true);

        Assert.Contains("только в Windows 11", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Нет_эффектов_нет_и_двойной_обработки()
    {
        string text = CaptureEffects.Summarize([], null, ownEcho: true, ownNoise: true);

        Assert.EndsWith("нет", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ДВОЙНАЯ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Voice_Clarity_поверх_нашего_шумодава_называется_двойной()
    {
        string text = CaptureEffects.Summarize(
            [
                new(CaptureEffects.DeepNoiseSuppression, CanSetState: true, IsOn: true),
                new(CaptureEffects.EchoCancellation, CanSetState: false, IsOn: true),
            ],
            null,
            ownEcho: true,
            ownNoise: true);

        Assert.Contains("шумодав ИИ (Voice Clarity) (переключаемый)", text, StringComparison.Ordinal);
        Assert.Contains("ДВОЙНАЯ обработка: наш эхоподавление и шумодав", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Наш_выключен_двойной_нет()
    {
        // Гарнитура: наш эхоподавитель выключен — системный один.
        string text = CaptureEffects.Summarize(
            [new(CaptureEffects.EchoCancellation, CanSetState: false, IsOn: true)],
            null,
            ownEcho: false,
            ownNoise: true);

        Assert.DoesNotContain("ДВОЙНАЯ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Выключенные_и_незнакомые_видны()
    {
        string text = CaptureEffects.Summarize(
            [
                new(CaptureEffects.NoiseSuppression, CanSetState: true, IsOn: false),
                new(new Guid("12345678-0000-0000-0000-000000000000"), CanSetState: false, IsOn: true),
            ],
            null,
            ownEcho: true,
            ownNoise: true);

        Assert.Contains("12345678", text, StringComparison.Ordinal);
        Assert.Contains("выключены: шумодав (переключаемый)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ДВОЙНАЯ", text, StringComparison.Ordinal);
    }
}
