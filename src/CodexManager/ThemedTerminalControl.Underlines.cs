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
    private void ConfigureLinkUnderlines()
    {
        linkUnderlines = new LinkUnderlineLayer(this) { IsHitTestVisible = false, ClipToBounds = true };
        Grid.SetColumn(linkUnderlines, 0); Children.Add(linkUnderlines);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ModelProperty)
            {
                if (underlineModel is { } old && old.UpdateUI == underlineRefresh) old.UpdateUI = null;
                underlineModel = Model;
                if (Model is { } model)
                {
                    var original = model.UpdateUI;
                    underlineRefresh = () => { original?.Invoke(); Dispatcher.UIThread.Post(() => linkUnderlines.InvalidateVisual(), DispatcherPriority.Render); };
                    model.UpdateUI = underlineRefresh;
                }
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
