using Avalonia.Controls;
using Avalonia.Media;

namespace CodexManager;

public partial class MainView
{
    private void OpenSchedule()
    {
        if (current is not { IsRemote: false, Archived: false } chat) return;
        var schedules = remoteSessions.Schedules;
        var initial = DateTimeOffset.Now.AddHours(1);
        var date = new CalendarDatePicker { SelectedDate = initial.Date };
        var time = new TimePicker { SelectedTime = new TimeSpan(initial.Hour, initial.Minute, 0) };
        var zone = TimeZoneInfo.Local;
        var repeat = new ComboBox { ItemsSource = new[] { "Once", "Daily on selected days", "Every X hours on selected days" }, SelectedIndex = 0 };
        var hours = new NumericUpDown { Minimum = 1, Maximum = int.MaxValue, Value = 2, FormatString = "0", Increment = 1 };
        var end = new CalendarDatePicker();
        var endLabel = new TextBlock();
        var days = Enum.GetValues<DayOfWeek>().Select(day => new CheckBox { Content = day.ToString()[..3], IsChecked = true }).ToArray();
        var dayPanel = new WrapPanel();
        foreach (var day in days) dayPanel.Children.Add(day);
        var dayLabel = new TextBlock { Text = "Repeat on" };
        var hourLabel = new TextBlock { Text = "Hours between sends" };
        void UpdateMode()
        {
            dayPanel.IsVisible = dayLabel.IsVisible = end.IsVisible = endLabel.IsVisible = repeat.SelectedIndex > 0;
            hours.IsVisible = hourLabel.IsVisible = repeat.SelectedIndex == 2;
            endLabel.Text = hours.IsVisible ? "End date (required, inclusive)" : "End date (optional, inclusive)";
        }
        repeat.SelectionChanged += (_, _) => UpdateMode();
        UpdateMode();
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var saved = new StackPanel { Spacing = 4 };
        void Refresh()
        {
            saved.Children.Clear();
            foreach (var send in schedules.ForChat(chat.Id))
            {
                var preview = send.Input.Text.Split('\n')[0];
                if (preview.Length == 0) preview = send.Input.Attachments.FirstOrDefault()?.Name ?? "";
                var row = new StackPanel { Spacing = 2 };
                row.Children.Add(new TextBlock { Text = preview, TextTrimming = TextTrimming.CharacterEllipsis });
                row.Children.Add(new TextBlock { Text = $"{send.Schedule.Repeat} · {send.Due.ToLocalTime():g}" });
                var cancel = new Button { Content = "Cancel scheduled send" };
                cancel.Click += (_, _) =>
                {
                    try { schedules.Remove(send.Id); Refresh(); }
                    catch (Exception ex) { error.Text = ex.Message; }
                };
                row.Children.Add(cancel);
                saved.Children.Add(row);
            }
        }
        var add = new Button { Content = "Schedule send", Classes = { "accent" } };
        var fields = new StackPanel
        {
            Width = 300, Spacing = 5,
            Children = { new TextBlock { Text = $"Start date and time ({zone.Id})", TextWrapping = TextWrapping.Wrap }, date, time, repeat,
                dayLabel, dayPanel, hourLabel, hours, endLabel, end,
                new TextBlock { Text = "Sends when the host is running and the chat is available. Repeating schedules do not replay every missed send.", TextWrapping = TextWrapping.Wrap },
                add, error, saved }
        };
        var flyout = new Flyout { Content = new ScrollViewer { MaxHeight = 440, Content = fields } };
        add.Click += (_, _) =>
        {
            if (!ReferenceEquals(current, chat)) { flyout.Hide(); return; }
            try
            {
                var text = Composer.Text?.Trim() ?? "";
                var attachments = OutgoingAttachments(chat);
                if (text.Length == 0 && attachments.Length == 0) { error.Text = "Enter a message or add an attachment first."; return; }
                if (date.SelectedDate is not { } start || time.SelectedTime is not { } at || repeat.SelectedIndex < 0)
                { error.Text = "Select a start date, time, and repeat option."; return; }
                var mask = Enumerable.Range(0, days.Length).Where(i => days[i].IsChecked == true).Sum(i => 1 << i);
                var mode = (SendRepeat)repeat.SelectedIndex;
                var until = mode != SendRepeat.Once && end.SelectedDate is { } last ? DateOnly.FromDateTime(last) : (DateOnly?)null;
                if (mode == SendRepeat.Hourly && (until is null || hours.Value is not { } interval || interval != decimal.Truncate(interval)))
                { error.Text = "Enter a whole number of hours and select an end date."; return; }
                var schedule = new SendSchedule(start.Date + at, mode, mask, (int)(hours.Value ?? 0), until, zone.Id);
                if (!schedules.Add(chat.Id, new(text, attachments), schedule)) { error.Text = "Choose settings with at least one future send."; return; }
                Composer.Text = ""; chat.Draft = ""; chat.Attachments.Clear(); store.Save(chat); UpdateControls();
                flyout.Hide();
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        try { Refresh(); }
        catch (Exception ex) { error.Text = ex.Message; }
        flyout.ShowAt(chatComposer.ScheduleButton);
    }
}
