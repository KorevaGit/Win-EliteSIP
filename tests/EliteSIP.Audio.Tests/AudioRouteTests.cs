using EliteSIP.Audio;

namespace EliteSIP.Audio.Tests;

/// <summary>
/// Маршрут звука и то, что о нём надо сказать человеку.
///
/// Перенесено из <c>Packages/MediaCore/Tests/MediaCoreTests/AudioRouteTests.swift</c>.
/// Признак режима гарнитуры проверяется иначе, чем в оригинале: там его
/// вычисляли по появлению входных каналов у устройства вывода, здесь режим
/// связи — отдельная конечная точка, и вычислять нечего.
/// </summary>
public sealed class AudioRouteTests
{
    [Fact]
    public void Обычный_маршрут_ни_о_чём_не_предупреждает()
    {
        AudioRoute route = new(
            Input: Device("Микрофон гарнитуры", AudioTransport.Usb, AudioDeviceDirection.Capture, 48000),
            Output: Device("Наушники гарнитуры", AudioTransport.Usb, AudioDeviceDirection.Render, 48000));

        Assert.False(route.IsHeadsetMode);
        Assert.False(route.IsNarrowband);
        Assert.Equal("вход Микрофон гарнитуры → выход Наушники гарнитуры", route.Summary);
    }

    [Fact]
    public void Режим_связи_объявляется_человеку()
    {
        // Ради этого тип и существует: звук системы становится глуше, объяснить
        // это нечем, и вопрос «почему испортился звук» приходит к нам.
        AudioRoute route = new(
            Input: Device("AirPods Pro Hands-Free", AudioTransport.BluetoothHandsFree, AudioDeviceDirection.Capture, 8000),
            Output: Device("AirPods Pro Hands-Free", AudioTransport.BluetoothHandsFree, AudioDeviceDirection.Render, 8000));

        Assert.True(route.IsHeadsetMode);
        Assert.Contains("режим гарнитуры", route.Summary, StringComparison.Ordinal);
        Assert.Contains("это Bluetooth, не мы", route.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Узкая_полоса_объявляется_отдельно_от_режима_связи()
    {
        // Два разных сообщения, потому что это два разных факта. Режим связи
        // портит звук системы; узкая полоса портит сам разговор и делает
        // бессмысленным широкополосный кодек. На macOS второго не было вовсе:
        // там AirPods работали в широкой полосе.
        AudioRoute route = new(
            Input: Device("AirPods Pro Hands-Free", AudioTransport.BluetoothHandsFree, AudioDeviceDirection.Capture, 8000),
            Output: Device("AirPods Pro Hands-Free", AudioTransport.BluetoothHandsFree, AudioDeviceDirection.Render, 8000));

        Assert.True(route.IsNarrowband);
        Assert.Contains("узкая полоса", route.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Режима_связи_достаточно_с_одной_стороны()
    {
        // Пока микрофон гарнитуры открыт, в режим связи уходит весь канал.
        // Смотреть на обе стороны и требовать совпадения значило бы промолчать
        // ровно в момент, когда звук у человека испортился.
        AudioRoute route = new(
            Input: Device("AirPods Pro Hands-Free", AudioTransport.BluetoothHandsFree, AudioDeviceDirection.Capture, 8000),
            Output: Device("Динамики", AudioTransport.BuiltIn, AudioDeviceDirection.Render, 48000));

        Assert.True(route.IsHeadsetMode);
    }

    [Fact]
    public void Стерео_через_Bluetooth_режимом_связи_не_является()
    {
        // Слушать музыку в AirPods, говоря во встроенный микрофон, — законная
        // связка, и предупреждать тут не о чем.
        AudioRoute route = new(
            Input: Device("Набор микрофонов", AudioTransport.BuiltIn, AudioDeviceDirection.Capture, 48000),
            Output: Device("AirPods Pro Stereo", AudioTransport.Bluetooth, AudioDeviceDirection.Render, 44100));

        Assert.False(route.IsHeadsetMode);
        Assert.False(route.IsNarrowband);
    }

    [Fact]
    public void Отсутствие_устройства_не_роняет_сводку()
    {
        // Машина без микрофона — не авария: панель обязана показать маршрут и
        // сказать, чего именно нет.
        AudioRoute route = new(Input: null, Output: null);

        Assert.False(route.IsHeadsetMode);
        Assert.False(route.IsNarrowband);
        Assert.Equal("вход нет → выход нет", route.Summary);
    }

    private static AudioDevice Device(
        string name,
        AudioTransport transport,
        AudioDeviceDirection direction,
        int sampleRate) =>
        new(
            Id: name,
            Name: name,
            Direction: direction,
            Transport: transport,
            TransportName: transport.ToString(),
            Availability: AudioDeviceAvailability.Active,
            Channels: 1,
            SampleRate: sampleRate,
            FormatIsLive: true);
}
