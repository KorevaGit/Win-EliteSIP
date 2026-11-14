using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using EliteSIP.App.Theme;

namespace EliteSIP.App.Tests;

[Collection("Settings popup windows")]
public sealed class InteractiveResizeTests
{
    [Fact]
    public void Во_время_перетаскивания_убираются_только_тени_после_него_восстанавливаются() => WpfHost.Run(() =>
    {
        var shadow = new DropShadowEffect();
        var blur = new BlurEffect();
        var card = new Border { Effect = shadow };
        var image = new Border { Effect = blur };
        var root = new StackPanel();
        root.Children.Add(card);
        root.Children.Add(image);
        using (new InteractiveResize.ShadowLease(root))
        {
            Assert.Null(card.Effect);
            Assert.Same(blur, image.Effect);
        }
        Assert.Same(shadow, card.Effect);
        Assert.Same(blur, image.Effect);
    });

    [Fact]
    public void Возврат_тени_не_перекрывает_новое_состояние_стиля() => WpfHost.Run(() =>
    {
        var shadow = new DropShadowEffect();
        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(UIElement.EffectProperty, shadow));
        var trigger = new Trigger { Property = FrameworkElement.TagProperty, Value = "flat" };
        trigger.Setters.Add(new Setter(UIElement.EffectProperty, null));
        style.Triggers.Add(trigger);
        var card = new Border { Style = style };
        using (new InteractiveResize.ShadowLease(card))
        {
            Assert.Null(card.Effect);
            card.Tag = "flat";
        }
        Assert.Null(card.Effect);
        card.Tag = null;
        Assert.Same(shadow, card.Effect);
    });
}
