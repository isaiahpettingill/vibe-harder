namespace CodexManager;

internal sealed record ScheduledSendDraft(DateTime? Date, TimeSpan? Time, SendRepeat Repeat, int Days, decimal? Hours, bool LimitDaily, DateTime? End)
{
    public static ScheduledSendDraft New(DateTime now)
    {
        var start = now.AddHours(1);
        return new(start.Date, new TimeSpan(start.Hour, start.Minute, 0), SendRepeat.Once, 0b1111111, 2, false, null);
    }
}
