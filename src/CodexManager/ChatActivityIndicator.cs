using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;

namespace CodexManager;

public sealed class ChatActivityIndicator : Grid
{
    private readonly Chat chat;
    private readonly Image provider;
    private readonly Avalonia.Controls.Shapes.Path spinner;
    private readonly Ellipse unread;
    private readonly Avalonia.Controls.Shapes.Path warning;
    private readonly RotateTransform rotation = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private bool attached;

    public ChatActivityIndicator(Chat chat)
    {
        this.chat = chat;
        Width = Height = 14;
        provider = new Image { Source = BrandAssets.Provider(chat.Provider), Width = 14, Height = 14 };
        spinner = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M7,1 A6,6 0 1 1 1,7"), StrokeThickness = 1.6, RenderTransform = rotation, RenderTransformOrigin = RelativePoint.Center };
        spinner.Bind(Shape.StrokeProperty, this.GetResourceObservable("AppAccent"));
        unread = new Ellipse { Width = 6, Height = 6, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        unread.Bind(Shape.FillProperty, this.GetResourceObservable("AppAccent"));
        warning = new Avalonia.Controls.Shapes.Path { Name = "PermissionWarning", Data = Geometry.Parse("M7,1 L13,13 L1,13 Z M7,5 L7,9 M7,10 L7,11"), Stroke = Brushes.Goldenrod, StrokeThickness = 1.4 };
        Children.Add(provider); Children.Add(spinner); Children.Add(unread); Children.Add(warning);
        timer.Tick += (_, _) => rotation.Angle = (rotation.Angle + 24) % 360;
        Update();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e); attached = true; chat.PropertyChanged += ChatChanged; Update();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        attached = false; timer.Stop(); chat.PropertyChanged -= ChatChanged; base.OnDetachedFromVisualTree(e);
    }
    private void ChatChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Chat.Busy) or nameof(Chat.HasUnreadCompletion) or nameof(Chat.NeedsPermission)) Update();
    }
    private void Update()
    {
        warning.IsVisible = chat.NeedsPermission;
        spinner.IsVisible = chat.Busy && !chat.NeedsPermission;
        unread.IsVisible = !chat.NeedsPermission && !chat.Busy && chat.HasUnreadCompletion;
        provider.IsVisible = !chat.NeedsPermission && !chat.Busy && !chat.HasUnreadCompletion;
        ToolTip.SetTip(this, chat.NeedsPermission ? "Needs permission" : chat.Busy ? "Chat in progress" : chat.HasUnreadCompletion ? "New completed reply" : chat.ProviderLabel);
        if (attached && spinner.IsVisible) timer.Start(); else timer.Stop();
    }
}
