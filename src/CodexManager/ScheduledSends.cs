using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;

namespace CodexManager;

internal enum SendRepeat { Once, Daily, Hourly }

internal sealed record SendSchedule(DateTime Start, SendRepeat Repeat, int Days, int Hours, DateOnly? End, string Zone)
{
    public DateTimeOffset? Next(DateTimeOffset after)
    {
        if (Repeat != SendRepeat.Once && (Days == 0 || Repeat == SendRepeat.Hourly && (Hours <= 0 || End is null))) return null;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Zone);
        var step = (long)Math.Min((decimal)(Repeat == SendRepeat.Hourly ? Hours : 24) * TimeSpan.TicksPerHour, long.MaxValue);
        var local = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        var index = Repeat == SendRepeat.Once ? 0 : Math.Max(0, (local - Start).Ticks / step - 1);
        // Integer-hour slots repeat their weekday pattern within 168 steps.
        for (var i = 0; i < (Repeat == SendRepeat.Once ? 1 : 340); i++, index++)
        {
            if (index > (DateTime.MaxValue - Start).Ticks / step) break;
            var slot = DateTime.SpecifyKind(Start.AddTicks(index * step), DateTimeKind.Unspecified);
            if (End is { } end && DateOnly.FromDateTime(slot) > end) break;
            if (Repeat != SendRepeat.Once && (Days & (1 << (int)slot.DayOfWeek)) == 0 || zone.IsInvalidTime(slot)) continue;
            var due = new DateTimeOffset(slot, zone.GetUtcOffset(slot));
            if (due > after) return due;
        }
        return null;
    }
}

internal sealed record ScheduledSend(string Id, string ChatId, PendingInput Input, SendSchedule Schedule, DateTimeOffset Due);

[JsonSerializable(typeof(ScheduledSend[]))]
[JsonSerializable(typeof(ScheduledSendDraft))]
internal partial class ScheduledSendJson : JsonSerializerContext;

internal sealed class ScheduledSends(Store store, IList<Workspace> workspaces, IList<Chat> chats, Func<Chat, Workspace, ChatRuntime> runtime) : IDisposable
{
    private const string Key = "scheduledSends";
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private ScheduledSend[]? entries;
    private ScheduledSend[] Entries => entries ??= JsonSerializer.Deserialize(store.Setting(Key) ?? "[]", ScheduledSendJson.Default.ScheduledSendArray) ?? [];

    public event Action? Changed;

    public void Start()
    {
        timer.Tick += (_, _) => Dispatch();
        timer.Start();
    }

    public IEnumerable<ScheduledSend> ForChat(string id) => Entries.Where(send => send.ChatId == id).OrderBy(send => send.Due);

    public ScheduledSendDraft? DraftFor(string chatId) => store.Setting(DraftKey(chatId)) is { Length: > 0 } value
        ? JsonSerializer.Deserialize(value, ScheduledSendJson.Default.ScheduledSendDraft) : null;

    public void SaveDraft(string chatId, ScheduledSendDraft? draft) => store.Setting(DraftKey(chatId), draft is null
        ? "" : JsonSerializer.Serialize(draft, ScheduledSendJson.Default.ScheduledSendDraft));

    private static string DraftKey(string chatId) => "scheduledSendDraft:" + chatId;

    public bool Add(string chatId, PendingInput input, SendSchedule schedule)
    {
        if (schedule.Next(DateTimeOffset.UtcNow) is not { } due) return false;
        Save([.. Entries, new(Guid.NewGuid().ToString("N"), chatId, input, schedule, due)]);
        return true;
    }

    public void Remove(string id) => Save(Entries.Where(send => send.Id != id).ToArray());

    private void Save(ScheduledSend[] updated)
    {
        store.Setting(Key, JsonSerializer.Serialize(updated, ScheduledSendJson.Default.ScheduledSendArray));
        entries = updated;
        Changed?.Invoke();
    }

    private void Dispatch()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var send in Entries.Where(send => send.Due <= now).OrderBy(send => send.Due))
        {
            try
            {
                if (send.Schedule.End is { } end && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(send.Schedule.Zone)).DateTime) > end)
                {
                    Remove(send.Id);
                    continue;
                }
                var chat = chats.FirstOrDefault(chat => chat.Id == send.ChatId);
                if (chat is null || chat.Archived || chat.Busy || chat.NeedsLogin || workspaces.FirstOrDefault(owner => owner.Id == chat.WorkspaceId && !owner.IsRemote) is not { } workspace) continue;
                var active = runtime(chat, workspace);
                if (active.IsRecovering || active.IsReconnecting || active.IsConfiguring || active.IsChangingHistory) continue;
                // Advance before handing off so a host restart cannot replay the same send.
                var next = send.Schedule.Repeat == SendRepeat.Once ? null : send.Schedule.Next(now);
                Save(Entries.SelectMany(entry => entry.Id != send.Id ? new[] { entry } : next is { } due ? [entry with { Due = due }] : []).ToArray());
                _ = Deliver(active, send.Input);
            }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Scheduled send failed: {0}", error.Message); }
        }
    }

    private static async Task Deliver(ChatRuntime active, PendingInput input)
    {
        try { await active.Send(input.Text, input.Attachments); }
        catch (Exception error) { System.Diagnostics.Trace.TraceError("Scheduled send failed: {0}", error.Message); }
    }

    public void Dispose() => timer.Stop();
}
