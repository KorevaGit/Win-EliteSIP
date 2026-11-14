using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace EliteSIP.App.Theme;

/// <summary>Keep live resize inexpensive on software rendering and integrated GPUs.</summary>
internal static class InteractiveResize
{
    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached(
        "Attached", typeof(bool), typeof(InteractiveResize), new PropertyMetadata(false));

    internal static void Attach(Window window)
    {
        if ((bool)window.GetValue(AttachedProperty) || window.ResizeMode is not (ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
            || PresentationSource.FromVisual(window) is not HwndSource source) { return; }
        ShadowLease? lease = null;
        source.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if (message == 0x0214) { lease ??= new ShadowLease(window); } // WM_SIZING, not ordinary moving.
            if (message is 0x0232 or 0x001F) // WM_EXITSIZEMOVE / WM_CANCELMODE.
            {
                lease?.Dispose();
                lease = null;
            }
            return 0;
        });
        window.Closed += (_, _) => { lease?.Dispose(); lease = null; };
        window.SetValue(AttachedProperty, true);
    }

    internal sealed class ShadowLease : IDisposable
    {
        private readonly List<(UIElement Element, object Local, Effect Effect)> _shadows = [];

        internal ShadowLease(DependencyObject root) => Visit(root);

        private void Visit(DependencyObject node)
        {
            if (node is UIElement { Effect: DropShadowEffect effect } element)
            {
                _shadows.Add((element, element.ReadLocalValue(UIElement.EffectProperty), effect));
                element.SetCurrentValue(UIElement.EffectProperty, null);
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) { Visit(VisualTreeHelper.GetChild(node, i)); }
        }

        public void Dispose()
        {
            foreach (var (element, local, effect) in _shadows)
            {
                if (local == DependencyProperty.UnsetValue) { element.ClearValue(UIElement.EffectProperty); }
                else { element.SetCurrentValue(UIElement.EffectProperty, effect); }
            }
            _shadows.Clear();
        }
    }
}
