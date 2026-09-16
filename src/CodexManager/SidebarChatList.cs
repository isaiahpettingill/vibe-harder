using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;

namespace CodexManager;

// The sidebar owns scrolling. An inner ScrollViewer would give the virtualizing
// panel an unbounded viewport and realize every chat in an expanded workspace.
public sealed class SidebarChatList : ListBox
{
    public SidebarChatList()
    {
        Template = new FuncControlTemplate<SidebarChatList>((_, scope) =>
            new ItemsPresenter { Name = "PART_ItemsPresenter", ItemsPanel = ItemsPanel }.RegisterInNameScope(scope));
    }
}
