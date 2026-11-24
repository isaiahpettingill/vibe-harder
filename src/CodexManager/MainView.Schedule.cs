using Avalonia.Controls;
using Avalonia.Media;

namespace CodexManager;

public partial class MainView
{
    private Chat? scheduledChat;
    private bool scheduledSendsChanged;
    private ScheduledSend[] displayedScheduledSends = [];

    private void UpdateScheduledSends()
    {
        if (!scheduledSendsChanged && ReferenceEquals(scheduledChat, current)) return;
        scheduledSendsChanged = false;
        scheduledChat = current;
        var panel = chatComposer.ScheduledPanel;
        var rows = chatComposer.ScheduledItems;
        var schedules = remoteSessions.Schedules;
        var pending = current is { IsRemote: false } chat && chatComposer.ScheduleButton.IsVisible ? schedules.ForChat(chat.Id).ToArray() : [];
        if (displayedScheduledSends.SequenceEqual(pending)) return;
        displayedScheduledSends = pending;
        rows.Children.Clear();
        panel.IsVisible = pending.Length > 0;
        panel.Header = $"Scheduled sends ({pending.Length})";
        if (pending.Length == 0) return;
        var dayOrder = ScheduleDayOrder();
        foreach (var send in pending)
        {
            var preview = send.Input.Text.Split('\n')[0];
            if (preview.Length == 0) preview = send.Input.Attachments.FirstOrDefault()?.Name ?? "";
            var frequency = send.Schedule.Repeat switch
            {
                SendRepeat.Daily => $"Daily at {send.Schedule.Start:t}",
                SendRepeat.Hourly => $"Every {send.Schedule.Hours} hours",
                _ => "One-time send"
            };
            if (send.Schedule.Repeat is SendRepeat.Daily or SendRepeat.Hourly)
                frequency += " · " + string.Join(", ", dayOrder.Where(day => (send.Schedule.Days & (1 << (int)day)) != 0).Select(day => day.ToString()[..3]));
            if (send.Schedule.End is { } last) frequency += $" · Ends {last:d}";
            var timing = GitHubWorkflowRun.Parse(send.Schedule.WorkflowRunUrl) is { } run
                ? $"{(send.WaitingForRun ? "Waiting for" : "Finished:")} {run.Repository} · #{run.Id}"
                : $"{send.Due.ToLocalTime():g} · {frequency}";
            if (send.RunCheckFailed) timing += " · Can't check run; verify GitHub CLI access";
            var details = new StackPanel
            {
                Children = { new TextBlock { Text = preview, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = timing, TextWrapping = TextWrapping.Wrap, FontSize = 11, Classes = { "muted" } } }
            };
            var cancel = new IconButton { Name = "CancelScheduledSend", Icon = "remove", Label = "Cancel scheduled send" };
            cancel.Click += (_, _) =>
            {
                try { schedules.Remove(send.Id); }
                catch (Exception ex) { StatusText.Text = ex.Message; }
            };
            var edit = new IconButton { Name = "EditScheduledSend", Icon = "edit", Label = "Edit scheduled send" };
            var flyout = new Flyout();
            flyout.Opening += (_, _) =>
            {
                if (current is { } active && active.Id == send.ChatId)
                    flyout.Content = CreateScheduleEditor(active, flyout.Hide, send).Content;
            };
            edit.Flyout = flyout;
            Grid.SetColumn(edit, 1);
            Grid.SetColumn(cancel, 2);
            rows.Children.Add(new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 4, Children = { details, edit, cancel } });
        }
    }

    private void InitializeSchedule()
    {
        remoteSessions.Schedules.Changed += chatId =>
        {
            if (current is not { IsRemote: false } chat || chat.Id != chatId) return;
            scheduledSendsChanged = true;
            if (!closing && !uiSleeping) UpdateScheduledSends();
        };
        remoteSessions.Schedules.Start();
        var flyout = new Flyout();
        Action? saveDraft = null;
        flyout.Opening += (_, _) =>
        {
            if (current is { IsRemote: false, Archived: false } chat)
                (flyout.Content, saveDraft) = CreateScheduleEditor(chat, flyout.Hide);
        };
        flyout.Closed += (_, _) =>
        {
            saveDraft?.Invoke();
            saveDraft = null;
            flyout.Content = null;
        };
        chatComposer.ScheduleButton.Flyout = flyout;
    }

    private (Control Content, Action SaveDraft) CreateScheduleEditor(Chat chat, Action close, ScheduledSend? existing = null)
    {
        var schedules = remoteSessions.Schedules;
        var draft = existing is null ? schedules.DraftFor(chat.Id) : null;
        var initial = existing is { Schedule: var saved }
            ? saved.Repeat == SendRepeat.GitHubRun
                ? ScheduledSendDraft.New(DateTime.Now) with { Repeat = saved.Repeat, WorkflowRunUrl = saved.WorkflowRunUrl }
                : new ScheduledSendDraft(saved.Start.Date, saved.Start.TimeOfDay, saved.Repeat,
                    saved.Repeat == SendRepeat.Once ? 0b1111111 : saved.Days, saved.Hours > 0 ? saved.Hours : 2,
                    saved.Repeat == SendRepeat.Daily && saved.End is not null, saved.End?.ToDateTime(TimeOnly.MinValue))
            : draft ?? ScheduledSendDraft.New(DateTime.Now);
        var detectedRun = GitHubWorkflowRun.FindInMessage(existing?.Input.Text ?? Composer.Text);
        if (existing is null && draft is null && detectedRun is not null)
            initial = initial with { Repeat = SendRepeat.GitHubRun, WorkflowRunUrl = detectedRun.Url };
        var date = new CalendarDatePicker { SelectedDate = initial.Date };
        var time = new TimePicker { SelectedTime = initial.Time };
        var zone = existing?.Schedule.Zone ?? TimeZoneInfo.Local.Id;
        var repeat = new ComboBox { ItemsSource = new[] { "Once", "Daily on selected days", "Every X hours on selected days", "When GitHub Actions finishes" }, SelectedIndex = (int)initial.Repeat };
        var runLink = new TextBox { Name = "ScheduleRunLink", Text = initial.WorkflowRunUrl ?? detectedRun?.Url, PlaceholderText = "https://github.com/OWNER/REPO/actions/runs/…" };
        var runField = new StackPanel { Spacing = 6, Children = { new Label { Content = "GitHub Actions run link", Target = runLink }, runLink } };
        ToolTip.SetTip(runLink, "Uses GitHub CLI access on the workspace host.");
        var startLabel = new TextBlock { Text = $"Start date and time ({zone})", TextWrapping = TextWrapping.Wrap };
        var hours = new NumericUpDown { Minimum = 1, Maximum = int.MaxValue, Value = initial.Hours, FormatString = "0", Increment = 1 };
        var end = new CalendarDatePicker { SelectedDate = initial.End };
        var limitDaily = new CheckBox { Content = "End on a date", IsChecked = initial.LimitDaily };
        var endLabel = new TextBlock { Text = "Last day (inclusive)" };
        var dayOrder = ScheduleDayOrder();
        var days = dayOrder.Select(day =>
        {
            var check = new CheckBox { Content = day.ToString()[..3], IsChecked = (initial.Days & (1 << (int)day)) != 0 };
            Avalonia.Automation.AutomationProperties.SetName(check, day.ToString());
            return check;
        }).ToArray();
        var dayPanel = new WrapPanel();
        foreach (var day in days) dayPanel.Children.Add(day);
        var dayLabel = new TextBlock { Text = "Repeat on" };
        var hourLabel = new TextBlock { Text = "Hours between sends" };
        void UpdateMode()
        {
            var mode = (SendRepeat)repeat.SelectedIndex;
            runField.IsVisible = mode == SendRepeat.GitHubRun;
            date.IsVisible = time.IsVisible = startLabel.IsVisible = !runField.IsVisible;
            dayPanel.IsVisible = dayLabel.IsVisible = mode is SendRepeat.Daily or SendRepeat.Hourly;
            hours.IsVisible = hourLabel.IsVisible = mode == SendRepeat.Hourly;
            limitDaily.IsVisible = mode == SendRepeat.Daily;
            end.IsVisible = endLabel.IsVisible = hours.IsVisible || limitDaily.IsVisible && limitDaily.IsChecked == true;
            endLabel.Text = hours.IsVisible ? "Last day (required, inclusive)" : "Last day (inclusive)";
        }
        repeat.SelectionChanged += (_, _) => UpdateMode();
        limitDaily.IsCheckedChanged += (_, _) => UpdateMode();
        UpdateMode();
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Avalonia.Automation.AutomationProperties.SetLiveSetting(error, Avalonia.Automation.AutomationLiveSetting.Polite);
        int SelectedDays() => Enumerable.Range(0, days.Length).Where(i => days[i].IsChecked == true).Sum(i => 1 << (int)dayOrder[i]);
        var submitted = false;
        void SaveDraft()
        {
            if (submitted || existing is not null) return;
            var updated = new ScheduledSendDraft(date.SelectedDate, time.SelectedTime, (SendRepeat)repeat.SelectedIndex,
                SelectedDays(), hours.Value, limitDaily.IsChecked == true, end.SelectedDate) { WorkflowRunUrl = runLink.Text };
            if (updated == initial) return;
            try { schedules.SaveDraft(chat.Id, updated); }
            catch (Exception ex) { StatusText.Text = ex.Message; }
        }
        var add = new Button { Name = "ConfirmScheduleSend", Content = existing is null ? "Schedule send" : "Save", Classes = { "accent" } };
        var fields = new StackPanel { Width = 280, Spacing = 6 };
        fields.Children.Add(new TextBlock { Text = existing is null ? "Schedule message" : "Edit scheduled send", FontWeight = FontWeight.SemiBold });
        TextBox? message = null;
        if (existing is not null)
        {
            message = new TextBox { Text = existing.Input.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 120 };
            fields.Children.Add(new Label { Content = "Message", Target = message });
            fields.Children.Add(message);
        }
        foreach (var field in new Control[] { repeat, runField, startLabel, date, time,
            dayLabel, dayPanel, hourLabel, hours, limitDaily, endLabel, end, error, add }) fields.Children.Add(field);
        add.Click += (_, _) =>
        {
            if (!ReferenceEquals(current, chat)) { close(); return; }
            error.Text = null;
            try
            {
                var text = (message is null ? Composer.Text : message.Text)?.Trim() ?? "";
                var attachments = existing?.Input.Attachments ?? OutgoingAttachments(chat);
                if (text.Length == 0 && attachments.Length == 0) { error.Text = "Enter a message or add an attachment first."; return; }
                var mode = (SendRepeat)repeat.SelectedIndex;
                if (mode == SendRepeat.GitHubRun)
                {
                    if (GitHubWorkflowRun.Parse(runLink.Text) is not { } run)
                    { error.Text = "Paste a GitHub Actions run link."; return; }
                    SaveSend(text, attachments, SendSchedule.After(run));
                    return;
                }
                if (date.SelectedDate is not { } start || time.SelectedTime is not { } at || repeat.SelectedIndex < 0)
                { error.Text = "Select a start date, time, and repeat option."; return; }
                var mask = SelectedDays();
                if (mode != SendRepeat.Once && mask == 0) { error.Text = "Select at least one day."; return; }
                var until = end.IsVisible && end.SelectedDate is { } last ? DateOnly.FromDateTime(last) : (DateOnly?)null;
                if (mode == SendRepeat.Daily && limitDaily.IsChecked == true && until is null)
                { error.Text = "Select the last day, or turn off the end date."; return; }
                if (until is { } endDay && endDay < DateOnly.FromDateTime(start))
                { error.Text = "The last day cannot be before the start date."; return; }
                if (mode == SendRepeat.Hourly && (until is null || hours.Value is not { } interval || interval != decimal.Truncate(interval)))
                { error.Text = "Enter a whole number of hours and select an end date."; return; }
                var schedule = new SendSchedule(start.Date + at, mode, mode == SendRepeat.Once ? existing?.Schedule.Days ?? mask : mask,
                    mode == SendRepeat.Hourly ? (int)hours.Value!.Value : existing?.Schedule.Hours ?? 2, until, zone);
                SaveSend(text, attachments, schedule);
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        return (new ScrollViewer { MaxHeight = 440, Content = fields }, SaveDraft);

        void SaveSend(string text, Attachment[] attachments, SendSchedule schedule)
        {
            var accepted = existing is null ? schedules.Add(chat.Id, new(text, attachments), schedule) : schedules.Edit(existing.Id, text, schedule);
            if (!accepted) { error.Text = "Choose settings with at least one future send."; return; }
            submitted = true;
            if (existing is null)
            {
                schedules.SaveDraft(chat.Id, null);
                Composer.Text = ""; chat.Draft = ""; chat.Attachments.Clear(); store.Save(chat); UpdateControls();
            }
            close();
        }
    }

    private static DayOfWeek[] ScheduleDayOrder()
    {
        var first = (int)System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        return Enumerable.Range(0, 7).Select(i => (DayOfWeek)((first + i) % 7)).ToArray();
    }
}
