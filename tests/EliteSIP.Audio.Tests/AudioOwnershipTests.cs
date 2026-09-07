using EliteSIP.Audio;

namespace EliteSIP.Audio.Tests;

/// <summary>
/// Владение общим аудиотрактом.
///
/// Проверяется без звуковой карты намеренно: устройств в CI нет, а ошибка
/// здесь стоит заглушенного живого разговора — то есть того, что на живом
/// железе замечает уже собеседник.
///
/// Перенесено из <c>Packages/MediaCore/Tests/MediaCoreTests/AudioOwnershipTests.swift</c>.
/// Ключом там служила ссылочная тождественность, здесь — выданное значение,
/// поэтому объекты-заглушки не нужны, а вместо них добавлена проверка на
/// неповторимость ключей.
/// </summary>
public sealed class AudioOwnershipTests
{
    [Fact]
    public void Свободный_тракт_достаётся_тому_кто_попросил()
    {
        AudioOwnership ownership = new();
        var line = AudioOwnerToken.New();

        Assert.False(ownership.IsBusy);
        Assert.Equal(AudioOwnershipOutcome.Granted, ownership.Take(line));
        Assert.True(ownership.IsBusy);
        Assert.True(ownership.IsOwner(line));
    }

    [Fact]
    public void Снятая_линия_не_глушит_разговор_на_живой()
    {
        AudioOwnership ownership = new();
        var live = AudioOwnerToken.New();
        var removed = AudioOwnerToken.New();

        ownership.Take(live);

        // Ровно тот случай, ради которого написан тип: отложенная финализация
        // снятой линии приходит отпускать тракт через секунду после того, как
        // его забрала другая линия. Отпустить он не имеет права.
        Assert.False(ownership.Release(removed));
        Assert.True(ownership.IsOwner(live));
        Assert.True(ownership.IsBusy);
    }

    [Fact]
    public void Переключение_линий_отбирает_тракт_у_прежней()
    {
        AudioOwnership ownership = new();
        var first = AudioOwnerToken.New();
        var second = AudioOwnerToken.New();

        ownership.Take(first);
        Assert.Equal(AudioOwnershipOutcome.Replaced, ownership.Take(second));
        Assert.True(ownership.IsOwner(second));
        Assert.False(ownership.IsOwner(first));

        // И прежняя после этого не может отпустить чужое.
        Assert.False(ownership.Release(first));
        Assert.True(ownership.IsOwner(second));
    }

    [Fact]
    public void Повторный_захват_своего_же_тракта_не_ошибка()
    {
        AudioOwnership ownership = new();
        var line = AudioOwnerToken.New();

        ownership.Take(line);
        Assert.Equal(AudioOwnershipOutcome.AlreadyOwned, ownership.Take(line));
        Assert.True(ownership.IsOwner(line));
    }

    [Fact]
    public void Второй_отбой_по_той_же_линии_проходит_вхолостую()
    {
        AudioOwnership ownership = new();
        var line = AudioOwnerToken.New();

        ownership.Take(line);
        Assert.True(ownership.Release(line));
        Assert.False(ownership.Release(line));
        Assert.False(ownership.IsBusy);
    }

    [Fact]
    public void Отпущенный_тракт_достаётся_следующему()
    {
        AudioOwnership ownership = new();
        var first = AudioOwnerToken.New();
        var second = AudioOwnerToken.New();

        ownership.Take(first);
        ownership.Release(first);
        Assert.Equal(AudioOwnershipOutcome.Granted, ownership.Take(second));
    }

    [Fact]
    public void Ключи_не_повторяются()
    {
        // Проверка на замену ObjectIdentifier из оригинала. Там ключом был
        // адрес объекта, и после смерти линии он мог достаться следующей —
        // тогда чужой ключ признавался бы своим ровно в том случае, ради
        // которого весь тип и написан.
        HashSet<AudioOwnerToken> issued = [];
        for (int i = 0; i < 1000; i++)
        {
            Assert.True(issued.Add(AudioOwnerToken.New()), "ключ повторился");
        }
    }

    [Fact]
    public void Пустой_ключ_не_владеет_ничем()
    {
        AudioOwnership ownership = new();

        // Ключ по умолчанию — это «никто». Если бы им можно было взять тракт,
        // тракт остался бы занят навсегда: отпускать его было бы нечем.
        Assert.Throws<ArgumentException>(() => ownership.Take(default));
        Assert.False(ownership.IsBusy);

        // И на занятом тракте пустой ключ — не владелец и не освободитель.
        var line = AudioOwnerToken.New();
        ownership.Take(line);
        Assert.False(ownership.IsOwner(default));
        Assert.False(ownership.Release(default));
        Assert.True(ownership.IsOwner(line));
    }
}
