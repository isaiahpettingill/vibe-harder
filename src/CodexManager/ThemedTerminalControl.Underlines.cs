using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SvcSystems.UI.Terminal;

namespace CodexManager;

public sealed partial class ThemedTerminalControl
{
    private LinkUnderlineLayer? linkUnderlines;
    private TerminalControlModel? underlineModel;
    private Action? underlineRefresh;
    private Action? originalUnderlineRefresh;
    private bool underlinesAttached;
    private int underlinePending;
    private void UnhookUnderlines()
    {
        if (underlineModel is { } old && old.UpdateUI == underlineRefresh) old.UpdateUI = originalUnderlineRefresh;
        underlineModel = null; underlineRefresh = originalUnderlineRefresh = null;
    }
    private void HookUnderlines()
    {
        UnhookUnderlines();
        if (!underlinesAttached || Model is not { } model) return;
        underlineModel = model;
        var original = originalUnderlineRefresh = model.UpdateUI;
        underlineRefresh = () =>
        {
            original?.Invoke();
            if (Interlocked.Exchange(ref underlinePending, 1) != 0) return;
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref underlinePending, 0);
                if (underlinesAttached && IsEffectivelyVisible) linkUnderlines?.InvalidateVisual();
            }, DispatcherPriority.Render);
        };
        model.UpdateUI = underlineRefresh;
    }
    private void ConfigureLinkUnderlines()
    {
        linkUnderlines = new LinkUnderlineLayer(this) { IsHitTestVisible = false, ClipToBounds = true };
        Grid.SetColumn(linkUnderlines, 0); Children.Add(linkUnderlines);
        AttachedToVisualTree += (_, _) => { underlinesAttached = true; HookUnderlines(); };
        DetachedFromVisualTree += (_, _) => { underlinesAttached = false; UnhookUnderlines(); };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ModelProperty)
            {
                HookUnderlines();
            }
            if (e.Property == FontSizeProperty || e.Property == FontFamilyProperty || e.Property == ModelProperty) linkUnderlines.InvalidateVisual();
        };
    }
    private sealed class LinkUnderlineLayer(ThemedTerminalControl owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            var brush = Application.Current?.Resources["AppText"] as IBrush ?? Brushes.Gray;
            foreach (var rect in owner.LinkUnderlines()) context.DrawRectangle(brush, null, rect);
        }
    }
}
