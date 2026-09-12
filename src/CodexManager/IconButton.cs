using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace CodexManager;

public sealed class IconButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);
    private string? icon;
    private string? label;
    public string Icon
    {
        set
        {
            if (icon == value) return;
            icon = value;
            Content = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(value switch
                {
                    "command" => "M5,5 L11,5 L11,11 L5,11 Z M5,5 L3,5 A2,2 0 1 1 5,3 Z M11,5 L11,3 A2,2 0 1 1 13,5 Z M11,11 L13,11 A2,2 0 1 1 11,13 Z M5,11 L5,13 A2,2 0 1 1 3,11 Z",
                    "settings" => "M6,1 L10,1 L10,3 L12,4 L14,3 L16,7 L14,8 L14,10 L16,11 L14,15 L12,14 L10,15 L10,17 L6,17 L6,15 L4,14 L2,15 L0,11 L2,10 L2,8 L0,7 L2,3 L4,4 L6,3 Z M11,9 A3,3 0 1 1 5,9 A3,3 0 1 1 11,9",
                    "menu" => "M2,3 L14,3 M2,8 L14,8 M2,13 L14,13",
                    "refresh" => "M13,6 A6,6 0 1 0 14,10 M9,6 L14,6 L14,1",
                    "queue" => "M2,3 L14,3 M2,7 L10,7 M2,11 L8,11 M12,9 L12,15 M9,12 L15,12",
                    "folder" => "M1,4 L6,4 L8,6 L15,6 L14,14 L1,14 Z",
                    "latest" => "M8,2 L8,11 M4,7 L8,11 L12,7 M3,14 L13,14",
                    "chevron-up" => "M3,10 L8,5 L13,10",
                    "chevron-down" => "M3,5 L8,10 L13,5",
                    "chevron-right" => "M5,3 L10,8 L5,13",
                    "chevron-left" => "M10,3 L5,8 L10,13",
                    "terminal" => "M2,3 L7,8 L2,13 M9,13 L15,13",
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
    }
    public string Label { set { if (label == value) return; label = value; ToolTip.SetTip(this, value); AutomationProperties.SetName(this, value); } }
    public IconButton() { Padding = new Thickness(4); MinHeight = OperatingSystem.IsAndroid() ? 44 : 28; MinWidth = OperatingSystem.IsAndroid() ? 44 : 28; Icon = "copy"; }
}

