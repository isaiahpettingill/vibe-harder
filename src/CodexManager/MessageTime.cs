using System.Globalization;

namespace CodexManager;

public static class MessageTime
{
    public static string Format(DateTimeOffset timestamp, DateTimeOffset now)
    {
        var local = timestamp.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var age = now - timestamp;
        if (local.Date == today)
        {
            if (age.TotalMinutes < 1) return "Just now";
            if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} min ago";
            if (age.TotalHours < 2) return "1 hour ago";
            return local.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        if (local.Date == today.AddDays(-1)) return "Yesterday " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return local.ToString("d-M-yy '@' HH:mm", CultureInfo.InvariantCulture);
    }
}
