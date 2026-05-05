using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;


    internal class Alarm
    {
        private readonly ITelegramBotClient _bot;
        private readonly int _minutesBefore;

        public Alarm(ITelegramBotClient bot, int minutesBefore = 5)
        {
            _bot = bot;
            _minutesBefore = minutesBefore;
        }

        /// <summary>
        /// Запускает фоновый цикл уведомлений.
        /// Каждую минуту проверяет, есть ли урок, начинающийся ровно через _minutesBefore минут,
        /// и рассылает уведомления всем ученикам соответствующего класса.
        /// </summary>
        public Task RunAsync(CancellationToken ct) => Task.Run(async () =>
        {
            Console.WriteLine($"[Alarm] Запущен. Уведомления за {_minutesBefore} мин до урока.");

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
                var now = DateTime.Now;
                var nextMinute = now.AddSeconds(60 - now.Second).AddMilliseconds(-now.Millisecond);
                var delay = nextMinute - DateTime.Now;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct);
            }

            Console.WriteLine("[Alarm] Остановлен.");
        }, ct);

        private async Task CheckAndNotifyAsync(CancellationToken ct)
        {
            var now = DateTime.Now;
            string today = now.DayOfWeek.ToString();  // "Monday", "Tuesday" и т.д.

            // Время, которое будет через _minutesBefore минут, в формате "HH:mm"
            string targetTime = now.AddMinutes(_minutesBefore).ToString("HH:mm");

            using var db = new SchoolContext();

            // Находим все уроки, начинающиеся ровно через N минут
            var upcomingLessons = db.Schedules
                .Where(s => s.DayOfWeek == today && s.StartTime == targetTime)
                .ToList();

            if (!upcomingLessons.Any()) return;

            Console.WriteLine($"[Alarm] Найдено {upcomingLessons.Count} урок(а) в {targetTime}");

            // Группируем по классу и уведомляем всех учеников
            var byClass = upcomingLessons.GroupBy(l => l.ClassName);

            foreach (var group in byClass)
            {
                var lesson = group.First();
                var students = db.Users
                    .Where(u => u.ClassName == group.Key)
                    .ToList();

                string msg = $"🔔 Через {_minutesBefore} мин начнётся урок!\n" +
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
