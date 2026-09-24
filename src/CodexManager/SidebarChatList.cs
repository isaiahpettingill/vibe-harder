using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace CodexManager;

// The sidebar owns scrolling. An inner ScrollViewer would give the virtualizing
// panel an unbounded viewport and realize every chat in an expanded workspace.
public sealed class SidebarChatList : ListBox
{
    public Func<IPointer, bool>? TouchSwipe { get; set; }

    public override bool UpdateSelectionFromEvent(Control container, RoutedEventArgs eventArgs)
    {
        if (eventArgs is PointerEventArgs pointerEvent && TouchSwipe?.Invoke(pointerEvent.Pointer) == true) return false;
        return base.UpdateSelectionFromEvent(container, eventArgs);
    }

    public SidebarChatList()
    {
        Template = new FuncControlTemplate<SidebarChatList>((_, scope) =>
            new ItemsPresenter { Name = "PART_ItemsPresenter", ItemsPanel = ItemsPanel }.RegisterInNameScope(scope));
    }
}
