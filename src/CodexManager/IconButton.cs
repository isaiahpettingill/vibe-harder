using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace CodexManager;

public sealed class IconButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);
    public string Icon
    {
        set => Content = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(value switch
            {
                "collapse" => "M3,9 L8,4 L13,9 M3,14 L8,9 L13,14",
                "send" => "M8,14 L8,2 M3,7 L8,2 L13,7",
                "stop" => "M3,3 L13,3 L13,13 L3,13 Z",
                "add" => "M8,2 L8,14 M2,8 L14,8",
                "remove" => "M4,4 L12,12 M12,4 L4,12",
                "edit" => "M2,11 L11,2 L14,5 L5,14 L2,14 Z",
                "archive" => "M2,2 L14,2 L14,5 L2,5 Z M3,5 L3,14 L13,14 L13,5 M6,8 L10,8",
                "delete" => "M2,4 L14,4 M6,4 L6,1 L10,1 L10,4 M4,4 L4,14 L12,14 L12,4 M7,7 L7,11 M9,7 L9,11",
                "steer" => "M3,14 L3,8 C3,5 7,4 13,4 M9,1 L13,4 L9,8",
                _ => "M6,5 L14,5 L14,14 L6,14 Z M3,11 L2,11 L2,2 L10,2 L10,3"
            }),
            Width = 13,
            Height = 13,
            
            StrokeThickness = 1.3,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false
        };
    }
    public string Label { set { ToolTip.SetTip(this, value); AutomationProperties.SetName(this, value); } }
    public IconButton() { Padding = new Thickness(4); MinHeight = 22; MinWidth = 22; Icon = "copy"; }
}

