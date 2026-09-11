using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace CodexManager;

public sealed class AttachmentPreview : Expander
{
    public static readonly StyledProperty<Attachment?> AttachmentProperty = AvaloniaProperty.Register<AttachmentPreview, Attachment?>(nameof(Attachment));
    public Attachment? Attachment { get => GetValue(AttachmentProperty); set => SetValue(AttachmentProperty, value); }
    private Bitmap? bitmap;
    private int generation;
    private bool attached;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AttachmentProperty || change.Property == IsExpandedProperty) Refresh();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); attached = true; Refresh(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { attached = false; Refresh(); base.OnDetachedFromVisualTree(e); }
    private async void Refresh()
    {
        var version = ++generation;
        Content = null; bitmap?.Dispose(); bitmap = null;
        Header = Attachment?.Name; IsVisible = Attachment?.IsImage == true;
        if (!attached || !IsExpanded || Attachment is not { IsImage: true } attachment) return;
        try
        {
            var decoded = await Task.Run(() =>
            {
                using var stream = new MemoryStream(Convert.FromBase64String(attachment.Data));
                return Bitmap.DecodeToWidth(stream, 640);
            });
            if (version != generation) { decoded.Dispose(); return; }
            bitmap = decoded;
            Content = new Image { Source = bitmap, MaxHeight = 220, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform };
        }
        catch (Exception) { if (version == generation) Content = new TextBlock { Text = "Image preview unavailable" }; }
    }
}
