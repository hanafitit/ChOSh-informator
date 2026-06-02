using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using ЧОШ_информатор.Data;
using ЧОШ_информатор.Helpers;

namespace ЧОШ_информатор.Services;

public class AlarmService
{
    private readonly ITelegramBotClient _bot;

    public AlarmService(ITelegramBotClient bot)
    {
        _bot = bot;
    }

    public Task RunAsync(CancellationToken ct) => Task.Run(async () =>
    {
        Console.WriteLine("[Alarm] Запущен. Уведомления перед длинными переменами.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAndNotifyAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Alarm] Ошибка: {ex.Message}");
            }

            // Ждём до начала следующей минуты
            var now = TimeHelper.NowKz();
            var nextMinute = now.AddSeconds(60 - now.Second).AddMilliseconds(-now.Millisecond);
            var delay = nextMinute - TimeHelper.NowKz();
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);
        }

        Console.WriteLine("[Alarm] Остановлен.");
    }, ct);

    private async Task CheckAndNotifyAsync(CancellationToken ct)
    {
        var now = TimeHelper.NowKz();
        string today = now.DayOfWeek.ToString();
        string currentTime = now.ToString("HH:mm");

        using var db = new SchoolContext();

        // Берём всё расписание на сегодня, сгруппированное по классу
        var todayLessons = db.Schedules
            .Where(s => s.DayOfWeek == today)
            .ToList()
            .GroupBy(s => s.ClassName);

        foreach (var classGroup in todayLessons)
        {
            var lessons = classGroup.OrderBy(s => s.LessonNumber).ToList();

            for (int i = 0; i < lessons.Count; i++)
            {
                var lesson = lessons[i];

                // Вычисляем длину перемены перед этим уроком
                int breakMinutes = 0;
                if (i > 0)
                {
                    var prevLesson = lessons[i - 1];
                    var prevEnd = TimeSpan.Parse(prevLesson.EndTime);
                    var thisStart = TimeSpan.Parse(lesson.StartTime);
                    breakMinutes = (int)(thisStart - prevEnd).TotalMinutes;
                }

                // Перемена 5 минут или меньше — пропускаем
                if (breakMinutes <= 5) continue;

                // Уведомляем за 3 минуты до начала урока
                string notifyAt = TimeSpan.Parse(lesson.StartTime)
                    .Subtract(TimeSpan.FromMinutes(3))
                    .ToString(@"hh\:mm");

                if (currentTime != notifyAt) continue;

                Console.WriteLine($"[Alarm] Перемена {breakMinutes} мин перед {lesson.Subject} ({lesson.StartTime}), класс {classGroup.Key}");

                var students = db.Users
                    .Where(u => u.ClassName == classGroup.Key)
                    .ToList();

                string msg = $"🔔 Через 3 мин звонок!\n" +
                             $"📚 {lesson.Subject}\n" +
                             $"⏰ {lesson.StartTime} — {lesson.EndTime}";

                foreach (var student in students)
                {
                    try
                    {
                        await _bot.SendMessage(student.TelegramId, msg, cancellationToken: ct);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Alarm] Не удалось отправить {student.TelegramId}: {ex.Message}");
                    }
                }
            }
        }
    }
}
