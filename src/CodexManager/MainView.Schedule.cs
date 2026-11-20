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
        var limitDaily = new CheckBox { Content = "End on a date" };
        var endLabel = new TextBlock { Text = "Last day (inclusive)" };
        var cadence = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var firstDay = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var dayOrder = Enumerable.Range(0, 7).Select(i => (DayOfWeek)(((int)firstDay + i) % 7)).ToArray();
        var days = dayOrder.Select(day =>
        {
            var check = new CheckBox { Content = day.ToString()[..3], IsChecked = true };
            Avalonia.Automation.AutomationProperties.SetName(check, day.ToString());
            return check;
        }).ToArray();
        var dayPanel = new WrapPanel();
        foreach (var day in days) dayPanel.Children.Add(day);
        var dayLabel = new TextBlock { Text = "Repeat on" };
        var hourLabel = new TextBlock { Text = "Hours between sends" };
        void UpdateMode()
        {
            dayPanel.IsVisible = dayLabel.IsVisible = repeat.SelectedIndex > 0;
            hours.IsVisible = hourLabel.IsVisible = repeat.SelectedIndex == 2;
            limitDaily.IsVisible = repeat.SelectedIndex == 1;
            end.IsVisible = endLabel.IsVisible = hours.IsVisible || limitDaily.IsVisible && limitDaily.IsChecked == true;
            endLabel.Text = hours.IsVisible ? "Last day (required, inclusive)" : "Last day (inclusive)";
            cadence.IsVisible = hours.IsVisible;
            cadence.Text = "The interval starts at the chosen time. Sends that fall on unchecked days are skipped.";
        }
        repeat.SelectionChanged += (_, _) => UpdateMode();
        limitDaily.IsCheckedChanged += (_, _) => UpdateMode();
        UpdateMode();
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Avalonia.Automation.AutomationProperties.SetLiveSetting(error, Avalonia.Automation.AutomationLiveSetting.Polite);
        var saved = new StackPanel { Spacing = 8 };
        var scheduled = new Expander { Content = saved };
        void Refresh()
        {
            saved.Children.Clear();
            var pending = schedules.ForChat(chat.Id).ToArray();
            scheduled.Header = $"Scheduled sends ({pending.Length})";
            scheduled.IsVisible = pending.Length > 0;
            foreach (var send in pending)
            {
                var preview = send.Input.Text.Split('\n')[0];
                if (preview.Length == 0) preview = send.Input.Attachments.FirstOrDefault()?.Name ?? "";
                var row = new StackPanel { Spacing = 2 };
                row.Children.Add(new TextBlock { Text = preview, TextTrimming = TextTrimming.CharacterEllipsis });
                var frequency = send.Schedule.Repeat switch
                {
                    SendRepeat.Daily => $"Daily at {send.Schedule.Start:t}",
                    SendRepeat.Hourly => $"Every {send.Schedule.Hours} hours",
                    _ => "One-time send"
                };
                if (send.Schedule.Repeat != SendRepeat.Once)
                    frequency += " · " + string.Join(", ", dayOrder.Where(day => (send.Schedule.Days & (1 << (int)day)) != 0).Select(day => day.ToString()[..3]));
                if (send.Schedule.End is { } last) frequency += $" · Ends {last:d}";
                row.Children.Add(new TextBlock { Text = frequency, TextWrapping = TextWrapping.Wrap });
                row.Children.Add(new TextBlock { Text = $"Next send: {send.Due.ToLocalTime():g}" });
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
            Width = 280, Spacing = 6,
            Children = { new TextBlock { Text = "Schedule message", FontWeight = FontWeight.SemiBold }, scheduled, repeat,
                new TextBlock { Text = $"Start date and time ({zone.Id})", TextWrapping = TextWrapping.Wrap }, date, time,
                dayLabel, dayPanel, hourLabel, hours, cadence, limitDaily, endLabel, end,
                new TextBlock { Text = "Sends when the host is running and the chat is available. Repeating schedules do not replay every missed send.", TextWrapping = TextWrapping.Wrap },
                error, add }
        };
        var flyout = new Flyout { Content = new ScrollViewer { MaxHeight = 440, Content = fields } };
        add.Click += (_, _) =>
        {
            if (!ReferenceEquals(current, chat)) { flyout.Hide(); return; }
            error.Text = null;
            try
            {
                var text = Composer.Text?.Trim() ?? "";
                var attachments = OutgoingAttachments(chat);
                if (text.Length == 0 && attachments.Length == 0) { error.Text = "Enter a message or add an attachment first."; return; }
                if (date.SelectedDate is not { } start || time.SelectedTime is not { } at || repeat.SelectedIndex < 0)
                { error.Text = "Select a start date, time, and repeat option."; return; }
                var mask = Enumerable.Range(0, days.Length).Where(i => days[i].IsChecked == true).Sum(i => 1 << (int)dayOrder[i]);
                var mode = (SendRepeat)repeat.SelectedIndex;
                if (mode != SendRepeat.Once && mask == 0) { error.Text = "Select at least one day."; return; }
                var until = end.IsVisible && end.SelectedDate is { } last ? DateOnly.FromDateTime(last) : (DateOnly?)null;
                if (mode == SendRepeat.Daily && limitDaily.IsChecked == true && until is null)
                { error.Text = "Select the last day, or turn off the end date."; return; }
                if (until is { } endDay && endDay < DateOnly.FromDateTime(start))
                { error.Text = "The last day cannot be before the start date."; return; }
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
