using System;

namespace ЧОШ_информатор.Helpers;

public static class TimeHelper
{
    public static DateTime NowKz()
    {
        TimeZoneInfo tz;
        try
        {
            // Linux / macOS (Docker на Render, Railway и т.д.)
            tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Almaty");
        }
        catch
        {
            // Windows
            tz = TimeZoneInfo.FindSystemTimeZoneById("Central Asia Standard Time");
        }
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
    }
}
