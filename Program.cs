using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.EntityFrameworkCore;
using Octokit;

Console.OutputEncoding = Encoding.UTF8;

using var cts = new CancellationTokenSource();

var userStates = new Dictionary<long, string>();
var userNames  = new Dictionary<long, string>();
var userTemp   = new Dictionary<long, Dictionary<string, string>>();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("Остановка...");
    cts.Cancel();
};

try
{
    var token = Environment.GetEnvironmentVariable("BOT_TOKEN")
        ?? throw new Exception("BOT_TOKEN не задан!");

    var appUrl = Environment.GetEnvironmentVariable("APP_URL")
        ?? throw new Exception("APP_URL не задан!");

    var bot = new TelegramBotClient(token);

    var backup = new GitHubBackup(
        owner:   Environment.GetEnvironmentVariable("GH_OWNER")  ?? throw new Exception("GH_OWNER не задан"),
        repo:    Environment.GetEnvironmentVariable("GH_REPO")   ?? throw new Exception("GH_REPO не задан"),
        ghToken: Environment.GetEnvironmentVariable("GH_TOKEN")  ?? throw new Exception("GH_TOKEN не задан")
    );

    await backup.RestoreAsync();

    // Миграция — создаём новые таблицы и колонки если их нет
    DbMigrator.Migrate();

    _ = Task.Run(async () =>
    {
        while (!cts.Token.IsCancellationRequested)
        {
            var now  = DateTime.UtcNow;
            var next = DateTime.UtcNow.Date.AddDays(now.Hour >= 3 ? 1 : 0).AddHours(3);
            await Task.Delay(next - now, cts.Token);
            await backup.BackupAsync();
        }
    });

    var alarm = new Alarm(bot);
    _ = alarm.RunAsync(cts.Token);

    Console.WriteLine("BOT STARTING...");

    string webhookUrl = $"{appUrl.TrimEnd('/')}/bot";
    await bot.SetWebhook(webhookUrl, cancellationToken: cts.Token);
    Console.WriteLine($"Webhook установлен: {webhookUrl}");

    var me = await bot.GetMe();
    Console.WriteLine($"Бот запущен: @{me.Username}");

    await RunWebServer(bot, backup, cts.Token);
}
catch (OperationCanceledException) { Console.WriteLine("Бот остановлен."); }
catch (Exception ex)
{
    Console.WriteLine($"Критическая ошибка: {ex.Message}");
    Environment.Exit(1);
}

// ══════════════════════════════════════════════
// ВЕБ-СЕРВЕР
// ══════════════════════════════════════════════
async Task RunWebServer(ITelegramBotClient bot, GitHubBackup backup, CancellationToken ct)
{
    var port     = Environment.GetEnvironmentVariable("PORT") ?? "8080";
    var listener = new HttpListener();
    listener.Prefixes.Add($"http://+:{port}/");
    listener.Start();
    Console.WriteLine($"Веб-сервер запущен на порту {port}.");

    while (!ct.IsCancellationRequested)
    {
        try
        {
            var context = await listener.GetContextAsync();
            var req = context.Request;
            var res = context.Response;

            if (req.HttpMethod == "GET" && req.Url?.AbsolutePath == "/getdb")
            {
                var key       = req.QueryString["key"];
                var secretKey = Environment.GetEnvironmentVariable("DB_KEY");
                if (key != secretKey) { res.StatusCode = 403; res.OutputStream.Close(); continue; }

                byte[] dbBytes = await File.ReadAllBytesAsync("school.db");
                res.ContentType = "application/octet-stream";
                res.AddHeader("Content-Disposition", "attachment; filename=school.db");
                res.ContentLength64 = dbBytes.Length;
                await res.OutputStream.WriteAsync(dbBytes, ct);
                res.OutputStream.Close();
                continue;
            }

            if (req.HttpMethod == "POST" && req.Url?.AbsolutePath == "/bot")
            {
                using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                string json = await reader.ReadToEndAsync();
                res.StatusCode = 200;
                res.OutputStream.Close();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var update = JsonSerializer.Deserialize<Update>(json,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (update != null)
                            await HandleUpdate(bot, backup, update, ct);
                    }
                    catch (Exception ex) { Console.WriteLine($"Ошибка обработки update: {ex.Message}"); }
                }, ct);
                continue;
            }

            res.StatusCode = 404;
            res.OutputStream.Close();
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex) { Console.WriteLine($"Ошибка веб-сервера: {ex.Message}"); }
    }

    listener.Stop();
}

// ══════════════════════════════════════════════
// ОБРАБОТЧИК СООБЩЕНИЙ
// ══════════════════════════════════════════════
async Task HandleUpdate(ITelegramBotClient botClient, GitHubBackup backup, Update update, CancellationToken ct)
{
    string adminId = Environment.GetEnvironmentVariable("ADMIN_Id")
        ?? throw new Exception("ADMIN_Id не задан!");

    var msg    = update.Message;
    var chatId = msg?.Chat.Id ?? update.CallbackQuery?.Message?.Chat.Id ?? 0;
    if (chatId == 0) return;

    bool isAdmin = chatId.ToString() == adminId;

    string? text    = msg?.Text ?? msg?.Caption;
    string? photoId = msg?.Photo?.LastOrDefault()?.FileId;

    using var db = new SchoolContext();
    var teacher  = db.Teachers.FirstOrDefault(t => t.TelegramId == chatId);
    bool isTeacher = teacher != null;

    // ══════════════════════════════════════════════════════════════════════
    // МНОГОШАГОВЫЕ СОСТОЯНИЯ
    // ══════════════════════════════════════════════════════════════════════
    if (userStates.TryGetValue(chatId, out var currentState))
    {
        // ── Объявление ────────────────────────────────────────────────────
        if (currentState == "waitingAnnouncement" && isAdmin)
        {
            if (text == null && photoId == null)
            {
                await botClient.SendMessage(chatId, "Отправь текст или фото (можно с подписью).", cancellationToken: ct);
                return;
            }
            userStates.Remove(chatId);

            var allUsers = db.Users.ToList();
            int sent = 0, failed = 0;
            foreach (var u in allUsers)
            {
                try
                {
                    if (photoId != null)
                        await botClient.SendPhoto(u.TelegramId, InputFile.FromFileId(photoId),
                            caption: text, cancellationToken: ct);
                    else
                        await botClient.SendMessage(u.TelegramId, text!, cancellationToken: ct);
                    sent++;
                }
                catch { failed++; }
            }
            await botClient.SendMessage(chatId,
                $"✅ Объявление отправлено!\n👤 Доставлено: {sent}\n❌ Ошибок: {failed}",
                replyMarkup: AdminKeyboard(), cancellationToken: ct);
            return;
        }

        // ── Добавление учителя: имя ───────────────────────────────────────
        if (currentState == "teacher_add_name" && isAdmin)
        {
            if (text == null) return;
            userTemp[chatId] = new Dictionary<string, string> { ["name"] = text };
            userStates[chatId] = "teacher_add_id";
            await botClient.SendMessage(chatId, "Введи Telegram ID учителя:",
                replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
            return;
        }

        // ── Добавление учителя: ID ────────────────────────────────────────
        if (currentState == "teacher_add_id" && isAdmin)
        {
            if (text == null || !long.TryParse(text.Trim(), out _))
            {
                await botClient.SendMessage(chatId, "Неверный формат. Введи числовой Telegram ID:", cancellationToken: ct);
                return;
            }
            userTemp[chatId]["telegramId"] = text.Trim();
            userStates[chatId] = "teacher_add_homeroom";

            var classesKb = new ReplyKeyboardMarkup(new[]
            {
                new[] { new KeyboardButton("5-6 класс"), new KeyboardButton("7"), new KeyboardButton("8") },
                new[] { new KeyboardButton("9"), new KeyboardButton("10"), new KeyboardButton("11") },
                new[] { new KeyboardButton("Нет") }
            })
            { ResizeKeyboard = true };
            await botClient.SendMessage(chatId, "Классный руководитель какого класса? (или «Нет»):",
                replyMarkup: classesKb, cancellationToken: ct);
            return;
        }

        // ── Добавление учителя: класс рук ─────────────────────────────────
        if (currentState == "teacher_add_homeroom" && isAdmin)
        {
            if (text == null) return;
            userTemp[chatId]["homeroom"] = text == "Нет" ? "" : text;
            userStates[chatId] = "teacher_add_subjects";
            await botClient.SendMessage(chatId,
                "Введи предметы и классы которые ведёт учитель.\n" +
                "Формат — каждый предмет с новой строки:\n" +
                "<b>математика|10</b>\n<b>математика|11</b>\n<b>география|5-6 класс</b>\n\n" +
                "Отправь всё одним сообщением.",
                parseMode: ParseMode.Html,
                replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
            return;
        }

        // ── Добавление учителя: предметы ──────────────────────────────────
        if (currentState == "teacher_add_subjects" && isAdmin)
        {
            if (text == null) return;
            var tmp = userTemp[chatId];
            long tid = long.Parse(tmp["telegramId"]);
            string tname    = tmp["name"];
            string homeroom = tmp["homeroom"];

            var existing = db.Teachers.FirstOrDefault(t => t.TelegramId == tid);
            if (existing != null)
            {
                await botClient.SendMessage(chatId, "⚠️ Учитель с таким ID уже существует.",
                    replyMarkup: AdminKeyboard(), cancellationToken: ct);
                userStates.Remove(chatId);
                userTemp.Remove(chatId);
                return;
            }

            db.Teachers.Add(new Teacher
            {
                TelegramId    = tid,
                Name          = tname,
                IsHomeroom    = !string.IsNullOrEmpty(homeroom),
                HomeroomClass = homeroom
            });

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            int added = 0;
            foreach (var line in lines)
            {
                var parts = line.Split('|');
                if (parts.Length == 2)
                {
                    db.TeacherSubjects.Add(new TeacherSubject
                    {
                        TeacherName = tname,
                        Subject     = parts[0].Trim(),
                        ClassName   = parts[1].Trim()
                    });
                    added++;
                }
            }

            db.SaveChanges();
            userStates.Remove(chatId);
            userTemp.Remove(chatId);

            await botClient.SendMessage(chatId,
                $"✅ Учитель <b>{tname}</b> добавлен!\n📚 Предметов: {added}\n🏫 Класс рук.: {(string.IsNullOrEmpty(homeroom) ? "нет" : homeroom)}",
                parseMode: ParseMode.Html, replyMarkup: AdminKeyboard(), cancellationToken: ct);
            return;
        }

        // ── Удаление учителя ──────────────────────────────────────────────
        if (currentState == "teacher_delete" && isAdmin)
        {
            if (text == null) return;
            var toDelete = db.Teachers.FirstOrDefault(t => t.Name == text);
            if (toDelete == null)
                await botClient.SendMessage(chatId, "Учитель не найден.", replyMarkup: AdminKeyboard(), cancellationToken: ct);
            else
            {
                db.TeacherSubjects.RemoveRange(db.TeacherSubjects.Where(s => s.TeacherName == text));
                db.Teachers.Remove(toDelete);
                db.SaveChanges();
                await botClient.SendMessage(chatId, $"✅ Учитель {text} удалён.", replyMarkup: AdminKeyboard(), cancellationToken: ct);
            }
            userStates.Remove(chatId);
            return;
        }

        // ── Регистрация: имя ──────────────────────────────────────────────
        if (currentState == "waitingName" && text != null)
        {
            userNames[chatId]  = text;
            userStates[chatId] = "waitingClass";
            var classKb = new ReplyKeyboardMarkup(new[]
            {
                new[] { new KeyboardButton("5-6 класс") },
                new[] { new KeyboardButton("7"), new KeyboardButton("8") },
                new[] { new KeyboardButton("9"), new KeyboardButton("10"), new KeyboardButton("11") }
            })
            { ResizeKeyboard = true };
            await botClient.SendMessage(chatId, $"Отлично, {text}! 👇 Теперь выбери свой класс:",
                replyMarkup: classKb, cancellationToken: ct);
            return;
        }

        // ── Регистрация: класс ────────────────────────────────────────────
        if (currentState == "waitingClass" && text != null
            && new[] { "5-6 класс", "7", "8", "9", "10", "11" }.Contains(text))
        {
            string name      = userNames[chatId];
            string className = text;
            db.Users.Add(new User { TelegramId = chatId, FirstName = name, ClassName = className, Role = "student" });
            db.SaveChanges();
            userStates.Remove(chatId);
            userNames.Remove(chatId);
            string display = className == "5-6 класс" ? "5-6" : className;
            await botClient.SendMessage(chatId,
                $"✅ Готово, {name}! Ты зарегистрирован в {display} классе.",
                replyMarkup: MainKeyboard(), cancellationToken: ct);
            return;
        }
    }

    if (text == null) return;

    // ══════════════════════════════════════════════════════════════════════
    // SWITCH
    // ══════════════════════════════════════════════════════════════════════
    switch (text)
    {
        case "/start":
        {
            if (isAdmin)
            {
                await botClient.SendMessage(chatId, "👑 Добро пожаловать, администратор!",
                    replyMarkup: AdminKeyboard(), cancellationToken: ct);
                break;
            }
            if (isTeacher)
            {
                await botClient.SendMessage(chatId, $"👋 Добро пожаловать, {teacher!.Name}!",
                    replyMarkup: TeacherKeyboard(teacher), cancellationToken: ct);
                break;
            }
            var found = db.Users.FirstOrDefault(u => u.TelegramId == chatId);
            if (found != null)
                await botClient.SendMessage(chatId, "Главное меню:", replyMarkup: MainKeyboard(), cancellationToken: ct);
            else
            {
                await botClient.SendMessage(chatId,
                    "👋 Добро пожаловать! Для начала зарегистрируйся.\n\nВведи своё имя:",
                    replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
                userStates[chatId] = "waitingName";
            }
            break;
        }

        case "/admin":
            if (isAdmin)
                await botClient.SendMessage(chatId, "👑 Панель администратора:",
                    replyMarkup: AdminKeyboard(), cancellationToken: ct);
            else
                await botClient.SendMessage(chatId, "У вас нет прав", cancellationToken: ct);
            break;

        case "/backup":
            if (isAdmin)
            {
                await botClient.SendMessage(chatId, "⏳ Сохраняю БД...", cancellationToken: ct);
                await backup.BackupAsync();
                await botClient.SendMessage(chatId, "✅ БД сохранена в GitHub!", cancellationToken: ct);
            }
            else
                await botClient.SendMessage(chatId, "У вас нет прав", cancellationToken: ct);
            break;

        // ── ОБЪЯВЛЕНИЕ ────────────────────────────────────────────────────
        case "📢 Объявление":
            if (!isAdmin) break;
            userStates[chatId] = "waitingAnnouncement";
            await botClient.SendMessage(chatId,
                "📝 Отправь объявление — текст, фото или фото с подписью.",
                replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
            break;

        // ── УЧИТЕЛЯ (admin) ───────────────────────────────────────────────
        case "👨‍🏫 Учителя":
        {
            if (!isAdmin) break;
            var teacherKb = new ReplyKeyboardMarkup(new[]
            {
                new[] { new KeyboardButton("➕ Добавить учителя"), new KeyboardButton("➖ Удалить учителя") },
                new[] { new KeyboardButton("📋 Список учителей") },
                new[] { new KeyboardButton("⬅️ Назад") }
            })
            { ResizeKeyboard = true };
            await botClient.SendMessage(chatId, "Управление учителями:", replyMarkup: teacherKb, cancellationToken: ct);
            break;
        }

        case "➕ Добавить учителя":
            if (!isAdmin) break;
            userStates[chatId] = "teacher_add_name";
            await botClient.SendMessage(chatId, "Введи имя учителя:",
                replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
            break;

        case "➖ Удалить учителя":
        {
            if (!isAdmin) break;
            var teachers = db.Teachers.ToList();
            if (!teachers.Any()) { await botClient.SendMessage(chatId, "Учителей нет.", cancellationToken: ct); break; }
            var rows = teachers.Select(t => new[] { new KeyboardButton(t.Name) }).ToArray();
            userStates[chatId] = "teacher_delete";
            await botClient.SendMessage(chatId, "Выбери учителя для удаления:",
                replyMarkup: new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true }, cancellationToken: ct);
            break;
        }

        case "📋 Список учителей":
        {
            if (!isAdmin) break;
            var teachers = db.Teachers.ToList();
            if (!teachers.Any()) { await botClient.SendMessage(chatId, "Учителей пока нет.", cancellationToken: ct); break; }
            var sb = new StringBuilder("👨‍🏫 Учителя:\n\n");
            foreach (var t in teachers)
            {
                sb.AppendLine($"• {t.Name} (ID: {t.TelegramId})");
                if (t.IsHomeroom) sb.AppendLine($"  🏫 Классный рук.: {t.HomeroomClass}");
                foreach (var s in db.TeacherSubjects.Where(s => s.TeacherName == t.Name).ToList())
                    sb.AppendLine($"  📚 {s.Subject} — {s.ClassName}");
                sb.AppendLine();
            }
            await botClient.SendMessage(chatId, sb.ToString(), cancellationToken: ct);
            break;
        }

        // ── ПРОВЕРИТЬ РАСПИСАНИЕ (admin) ──────────────────────────────────
        case "📋 Проверить расписание":
        {
            if (!isAdmin) break;
            string today   = NowKz().DayOfWeek.ToString();
            string dateStr = NowKz().ToString("yyyy-MM-dd");

            var absentNames = db.TeacherAttendance
                .Where(a => a.Date == dateStr && !a.IsPresent)
                .Select(a => a.TeacherName).ToList();

            // Времена слотов на сегодня (глобальные по номеру урока)
            var timeSlots = db.Schedules
                .Where(s => s.DayOfWeek == today)
                .ToList()
                .GroupBy(s => s.LessonNumber)
                .Select(g => g.First())
                .OrderBy(s => s.LessonNumber)
                .ToList();

            var allClasses = db.Schedules
                .Where(s => s.DayOfWeek == today && s.ClassName != "")
                .Select(s => s.ClassName).Distinct().ToList();

            var sb = new StringBuilder($"📅 Расписание на сегодня ({today}):\n");
            if (absentNames.Any())
                sb.AppendLine($"❌ Отсутствуют: {string.Join(", ", absentNames)}\n");
            else
                sb.AppendLine("✅ Все учителя присутствуют\n");

            foreach (var cls in allClasses.OrderBy(c => c))
            {
                var lessons = db.Schedules
                    .Where(s => s.ClassName == cls && s.DayOfWeek == today)
                    .OrderBy(s => s.LessonNumber).ToList();

                var remaining = lessons.Where(l =>
                    string.IsNullOrEmpty(l.TeacherName) || !absentNames.Contains(l.TeacherName)
                ).ToList();

                if (!remaining.Any()) continue;

                sb.AppendLine($"🏫 {cls}:");
                for (int i = 0; i < remaining.Count; i++)
                {
                    var slot = timeSlots.ElementAtOrDefault(i);
                    string start = slot?.StartTime ?? remaining[i].StartTime;
                    string end   = slot?.EndTime   ?? remaining[i].EndTime;
                    sb.AppendLine($"  {i + 1}: {remaining[i].Subject} ({start} - {end})");
                }
                sb.AppendLine();
            }

            var confirmKb = new ReplyKeyboardMarkup(new[]
            {
                new[] { new KeyboardButton("✅ Подтвердить и разослать") },
                new[] { new KeyboardButton("⬅️ Назад") }
            })
            { ResizeKeyboard = true };

            await botClient.SendMessage(chatId, sb.ToString(), replyMarkup: confirmKb, cancellationToken: ct);
            break;
        }

        case "✅ Подтвердить и разослать":
        {
            if (!isAdmin) break;
            string today   = NowKz().DayOfWeek.ToString();
            string dateStr = NowKz().ToString("yyyy-MM-dd");

            var absentNames = db.TeacherAttendance
                .Where(a => a.Date == dateStr && !a.IsPresent)
                .Select(a => a.TeacherName).ToList();

            var timeSlots = db.Schedules
                .Where(s => s.DayOfWeek == today)
                .ToList()
                .GroupBy(s => s.LessonNumber)
                .Select(g => g.First())
                .OrderBy(s => s.LessonNumber)
                .ToList();

            var students = db.Users.ToList();
            int sent = 0, failed = 0;

            foreach (var student in students)
            {
                try
                {
                    var lessons = db.Schedules
                        .Where(s => s.ClassName == student.ClassName && s.DayOfWeek == today)
                        .OrderBy(s => s.LessonNumber).ToList();

                    var remaining = lessons.Where(l =>
                        string.IsNullOrEmpty(l.TeacherName) || !absentNames.Contains(l.TeacherName)
                    ).ToList();

                    if (!remaining.Any()) continue;

                    var sb = new StringBuilder($"📅 Новое расписание на сегодня ({today}):\n\n");
                    for (int i = 0; i < remaining.Count; i++)
                    {
                        var slot  = timeSlots.ElementAtOrDefault(i);
                        string start = slot?.StartTime ?? remaining[i].StartTime;
                        string end   = slot?.EndTime   ?? remaining[i].EndTime;
                        sb.AppendLine($"{i + 1}: {remaining[i].Subject} ({start} - {end})");
                    }

                    await botClient.SendMessage(student.TelegramId, sb.ToString(), cancellationToken: ct);
                    sent++;
                }
                catch { failed++; }
            }

            await botClient.SendMessage(chatId,
                $"✅ Расписание разослано!\n👤 Доставлено: {sent}\n❌ Ошибок: {failed}",
                replyMarkup: AdminKeyboard(), cancellationToken: ct);
            break;
        }

        // ── ПРИСУТСТВИЕ (учитель) ─────────────────────────────────────────
        case "✅ Буду на уроках":
        case "❌ Меня не будет":
        {
            if (!isTeacher) break;
            bool isPresent = text == "✅ Буду на уроках";
            string dateStr = NowKz().ToString("yyyy-MM-dd");

            var existing = db.TeacherAttendance
                .FirstOrDefault(a => a.Date == dateStr && a.TeacherName == teacher!.Name);
            if (existing != null)
                existing.IsPresent = isPresent;
            else
                db.TeacherAttendance.Add(new TeacherAttendance
                    { TeacherName = teacher!.Name, Date = dateStr, IsPresent = isPresent });
            db.SaveChanges();

            string reply = isPresent
                ? "✅ Отмечено: вы будете на уроках."
                : "❌ Отмечено: вас не будет. Администратор проверит расписание.";
            await botClient.SendMessage(chatId, reply, replyMarkup: TeacherKeyboard(teacher!), cancellationToken: ct);
            break;
        }

        // ── МОЁ РАСПИСАНИЕ (учитель) ──────────────────────────────────────
        case "🗓 Моё расписание":
        {
            if (!isTeacher) break;
            string today = NowKz().DayOfWeek.ToString();
            var subjects = db.TeacherSubjects.Where(s => s.TeacherName == teacher!.Name).ToList();

            var myLessons = db.Schedules
                .Where(s => s.DayOfWeek == today).ToList()
                .Where(s => subjects.Any(sub => sub.Subject == s.Subject && sub.ClassName == s.ClassName))
                .OrderBy(s => s.LessonNumber).ToList();

            if (!myLessons.Any()) { await botClient.SendMessage(chatId, "У вас нет уроков сегодня.", cancellationToken: ct); break; }

            var sb = new StringBuilder($"📅 Ваше расписание на сегодня ({today}):\n\n");
            foreach (var l in myLessons)
                sb.AppendLine($"{l.LessonNumber}: {l.Subject} ({l.ClassName}) {l.StartTime}–{l.EndTime}");
            await botClient.SendMessage(chatId, sb.ToString(), cancellationToken: ct);
            break;
        }

        // ── РАСПИСАНИЕ КЛАССА (классный рук) ──────────────────────────────
        case "🏫 Расписание класса":
        {
            if (!isTeacher || !teacher!.IsHomeroom) break;
            string today = NowKz().DayOfWeek.ToString();
            var lessons = db.Schedules
                .Where(s => s.ClassName == teacher.HomeroomClass && s.DayOfWeek == today)
                .OrderBy(s => s.LessonNumber).ToList();

            if (!lessons.Any()) { await botClient.SendMessage(chatId, "Уроков нет.", cancellationToken: ct); break; }

            var sb = new StringBuilder($"📅 Расписание {teacher.HomeroomClass} на сегодня ({today}):\n\n");
            foreach (var l in lessons)
                sb.AppendLine($"{l.LessonNumber}: {l.Subject} ({l.StartTime}–{l.EndTime})");
            await botClient.SendMessage(chatId, sb.ToString(), cancellationToken: ct);
            break;
        }

        // ── РАСПИСАНИЕ (ученик) ───────────────────────────────────────────
        case "📅 Расписание":
        {
            var kb = new ReplyKeyboardMarkup(new[]
            {
                new[] { new KeyboardButton("Сегодня"), new KeyboardButton("Завтра") },
                new[] { new KeyboardButton("Неделя") },
                new[] { new KeyboardButton("⬅️ Назад") }
            })
            { ResizeKeyboard = true };
            await botClient.SendMessage(chatId, "Выбери вариант:", replyMarkup: kb, cancellationToken: ct);
            break;
        }

        case "Сегодня":
        {
            var user = db.Users.FirstOrDefault(u => u.TelegramId == chatId);
            if (user == null) { await botClient.SendMessage(chatId, "Вы не зарегистрированы!", cancellationToken: ct); break; }
            string today = NowKz().DayOfWeek.ToString();
            var lessons = db.Schedules
                .Where(s => s.ClassName == user.ClassName && s.DayOfWeek == today)
                .OrderBy(s => s.LessonNumber).ToList();
            if (!lessons.Any()) { await botClient.SendMessage(chatId, "У вас нет уроков на сегодня.", cancellationToken: ct); break; }
            var sb = new StringBuilder($"Расписание на сегодня ({today}):\n");
            foreach (var item in lessons)
                sb.AppendLine($"{item.LessonNumber}: {item.Subject} ({item.StartTime} - {item.EndTime})");
            await botClient.SendMessage(chatId, sb.ToString(), cancellationToken: ct);
            break;
        }

        case "Завтра":
        {
            var user = db.Users.FirstOrDefault(u => u.TelegramId == chatId);
            if (user == null) { await botClient.SendMessage(chatId, "Вы не зарегистрированы!", cancellationToken: ct); break; }
            string tomorrow = NowKz().AddDays(1).DayOfWeek.ToString();
            var lessons = db.Schedules
                .Where(s => s.ClassName == user.ClassName && s.DayOfWeek == tomorrow)
                .OrderBy(s => s.LessonNumber).ToList();
            if (!lessons.Any()) { await botClient.SendMessage(chatId, "У вас нет уроков завтра.", cancellationToken: ct); break; }
            var sb = new StringBuilder($"Расписание на завтра ({tomorrow}):\n");
            foreach (var item in lessons)
                sb.AppendLine($"{item.LessonNumber}: {item.Subject} ({item.StartTime} - {item.EndTime})");
            await botClient.SendMessage(chatId, sb.ToString(), cancellationToken: ct);
            break;
        }

        case "Неделя":
        {
            var user = db.Users.FirstOrDefault(u => u.TelegramId == chatId);
            if (user == null) { await botClient.SendMessage(chatId, "Вы не зарегистрированы!", cancellationToken: ct); break; }
            var dayOrder = new Dictionary<string, int>
            {
                ["Monday"]=1,["Tuesday"]=2,["Wednesday"]=3,
                ["Thursday"]=4,["Friday"]=5,["Saturday"]=6,["Sunday"]=7
            };
            var lessons = db.Schedules
                .Where(s => s.ClassName == user.ClassName).ToList()
                .OrderBy(s => dayOrder.TryGetValue(s.DayOfWeek, out var o) ? o : 99)
                .ThenBy(s => s.LessonNumber).ToList();
            if (!lessons.Any()) { await botClient.SendMessage(chatId, "Расписание не найдено.", cancellationToken: ct); break; }
            var sb = new StringBuilder("Расписание на неделю:\n");
            string lastDay = "";
            foreach (var item in lessons)
            {
                if (item.DayOfWeek != lastDay) { sb.AppendLine($"\n📅 {item.DayOfWeek}:"); lastDay = item.DayOfWeek; }
                sb.AppendLine($"  {item.LessonNumber}: {item.Subject} ({item.StartTime} - {item.EndTime})");
            }
            await botClient.SendMessage(chatId, sb.ToString(), cancellationToken: ct);
            break;
        }

        case "📊 Опросы":
            await botClient.SendMessage(chatId, "Здесь будут опросы", cancellationToken: ct);
            break;

        case "👤 Профиль":
        {
            var user = db.Users.FirstOrDefault(u => u.TelegramId == chatId);
            if (user != null)
                await botClient.SendMessage(chatId,
                    $"👤 Имя: {user.FirstName}\n🏫 Класс: {user.ClassName}\n🆔 ID: {chatId}", cancellationToken: ct);
            else if (isTeacher)
                await botClient.SendMessage(chatId,
                    $"👤 {teacher!.Name}\n👨‍🏫 Учитель\n🆔 ID: {chatId}", cancellationToken: ct);
            else
                await botClient.SendMessage(chatId, $"🆔 ID: {chatId}", cancellationToken: ct);
            break;
        }

        case "⬅️ Назад":
            if (isAdmin)
                await botClient.SendMessage(chatId, "Главное меню:", replyMarkup: AdminKeyboard(), cancellationToken: ct);
            else if (isTeacher)
                await botClient.SendMessage(chatId, "Главное меню:", replyMarkup: TeacherKeyboard(teacher!), cancellationToken: ct);
            else
                await botClient.SendMessage(chatId, "Главное меню:", replyMarkup: MainKeyboard(), cancellationToken: ct);
            break;

        default:
            await botClient.SendMessage(chatId, "Используйте кнопки, чтобы управлять ботом", cancellationToken: ct);
            break;
    }

    if (update.CallbackQuery is { } query)
        await botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);
}

// ══════════════════════════════════════════════
// ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ
// ══════════════════════════════════════════════
static DateTime NowKz()
{
    TimeZoneInfo tz;
    try   { tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Almaty"); }
    catch { tz = TimeZoneInfo.FindSystemTimeZoneById("Central Asia Standard Time"); }
    return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
}

static ReplyKeyboardMarkup MainKeyboard() =>
    new(new[]
    {
        new[] { new KeyboardButton("📅 Расписание") },
        new[] { new KeyboardButton("📊 Опросы"), new KeyboardButton("👤 Профиль") }
    })
    { ResizeKeyboard = true };

static ReplyKeyboardMarkup AdminKeyboard() =>
    new(new[]
    {
        new[] { new KeyboardButton("📢 Объявление") },
        new[] { new KeyboardButton("👨‍🏫 Учителя"), new KeyboardButton("📋 Проверить расписание") },
        new[] { new KeyboardButton("👤 Профиль") }
    })
    { ResizeKeyboard = true };

static ReplyKeyboardMarkup TeacherKeyboard(Teacher t)
{
    var rows = new List<KeyboardButton[]>
    {
        new[] { new KeyboardButton("✅ Буду на уроках"), new KeyboardButton("❌ Меня не будет") },
        new[] { new KeyboardButton("🗓 Моё расписание") },
    };
    if (t.IsHomeroom)
        rows.Add(new[] { new KeyboardButton("🏫 Расписание класса") });
    rows.Add(new[] { new KeyboardButton("👤 Профиль") });
    return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true };
}

// ══════════════════════════════════════════════
// МИГРАЦИЯ БД
// ══════════════════════════════════════════════
public static class DbMigrator
{
    public static void Migrate()
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=school.db");
        conn.Open();

        // TeacherName в Schedule
        try
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE Schedule ADD COLUMN TeacherName TEXT NOT NULL DEFAULT ''";
            cmd.ExecuteNonQuery();
            Console.WriteLine("[Migration] Добавлена колонка TeacherName");
        }
        catch { /* уже есть */ }

        // Таблица Teachers
        var c1 = conn.CreateCommand();
        c1.CommandText = @"CREATE TABLE IF NOT EXISTS Teachers (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            TelegramId INTEGER NOT NULL,
            Name TEXT NOT NULL DEFAULT '',
            IsHomeroom INTEGER NOT NULL DEFAULT 0,
            HomeroomClass TEXT NOT NULL DEFAULT ''
        )";
        c1.ExecuteNonQuery();

        // Таблица TeacherSubjects
        var c2 = conn.CreateCommand();
        c2.CommandText = @"CREATE TABLE IF NOT EXISTS TeacherSubjects (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            TeacherName TEXT NOT NULL DEFAULT '',
            Subject TEXT NOT NULL DEFAULT '',
            ClassName TEXT NOT NULL DEFAULT ''
        )";
        c2.ExecuteNonQuery();

        // Таблица TeacherAttendance
        var c3 = conn.CreateCommand();
        c3.CommandText = @"CREATE TABLE IF NOT EXISTS TeacherAttendance (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            TeacherName TEXT NOT NULL DEFAULT '',
            Date TEXT NOT NULL DEFAULT '',
            IsPresent INTEGER NOT NULL DEFAULT 1
        )";
        c3.ExecuteNonQuery();

        Console.WriteLine("[Migration] БД актуальна.");
    }
}

// ══════════════════════════════════════════════
// GITHUB BACKUP
// ══════════════════════════════════════════════
public class GitHubBackup
{
    private readonly string _owner, _repo, _token;
    private const string DbPath   = "school.db";
    private const string FilePath = "backups/school.db";

    public GitHubBackup(string owner, string repo, string ghToken)
    { _owner = owner; _repo = repo; _token = ghToken; }

    public async Task BackupAsync()
    {
        try
        {
            var client  = CreateClient();
            byte[] bytes = await File.ReadAllBytesAsync(DbPath);
            string content = Convert.ToBase64String(bytes);

            RepositoryContentInfo? existing = null;
            try { existing = (await client.Repository.Content.GetAllContents(_owner, _repo, FilePath))[0]; }
            catch (NotFoundException) { }

            if (existing != null)
                await client.Repository.Content.UpdateFile(_owner, _repo, FilePath,
                    new UpdateFileRequest($"db backup {DateTime.UtcNow:yyyy-MM-dd HH:mm}", content, existing.Sha));
            else
                await client.Repository.Content.CreateFile(_owner, _repo, FilePath,
                    new CreateFileRequest($"db backup {DateTime.UtcNow:yyyy-MM-dd HH:mm}", content));

            Console.WriteLine($"[Backup] БД сохранена: {DateTime.UtcNow:HH:mm:ss}");
        }
        catch (Exception ex) { Console.WriteLine($"[Backup] Ошибка: {ex.Message}"); }
    }

    public async Task RestoreAsync()
    {
        try
        {
            var client   = CreateClient();
            var contents = await client.Repository.Content.GetAllContents(_owner, _repo, FilePath);
            string base64 = contents[0].EncodedContent
                .Replace("\n","").Replace("\r","").Replace(" ","");

            byte[] bytes = Convert.FromBase64String(base64);
            string decoded = Encoding.UTF8.GetString(bytes);
            if (decoded.StartsWith("U1FM") || !decoded.StartsWith("SQLite"))
                bytes = Convert.FromBase64String(decoded.Replace("\n","").Replace("\r","").Replace(" ",""));

            await File.WriteAllBytesAsync(DbPath, bytes);
            Console.WriteLine("[Restore] БД восстановлена.");
        }
        catch (NotFoundException) { Console.WriteLine("[Restore] Бэкапа нет — используется локальная БД."); }
        catch (Exception ex)      { Console.WriteLine($"[Restore] Ошибка: {ex.Message}"); }
    }

    private GitHubClient CreateClient()
    {
        var c = new GitHubClient(new ProductHeaderValue("SchoolBot"));
        c.Credentials = new Credentials(_token);
        return c;
    }
}

// ══════════════════════════════════════════════
// МОДЕЛИ
// ══════════════════════════════════════════════
public class Schedule
{
    public int    Id           { get; set; }
    public string ClassName    { get; set; } = "";
    public string DayOfWeek    { get; set; } = "";
    public int    LessonNumber { get; set; }
    public string Subject      { get; set; } = "";
    public string StartTime    { get; set; } = "";
    public string EndTime      { get; set; } = "";
    public string TeacherName  { get; set; } = "";
}

public class User
{
    public int    Id         { get; set; }
    public long   TelegramId { get; set; }
    public string FirstName  { get; set; } = "";
    public string ClassName  { get; set; } = "";
    public string Role       { get; set; } = "student";
}

public class Teacher
{
    public int    Id            { get; set; }
    public long   TelegramId    { get; set; }
    public string Name          { get; set; } = "";
    public bool   IsHomeroom    { get; set; }
    public string HomeroomClass { get; set; } = "";
}

public class TeacherSubject
{
    public int    Id          { get; set; }
    public string TeacherName { get; set; } = "";
    public string Subject     { get; set; } = "";
    public string ClassName   { get; set; } = "";
}

public class TeacherAttendance
{
    public int    Id          { get; set; }
    public string TeacherName { get; set; } = "";
    public string Date        { get; set; } = "";
    public bool   IsPresent   { get; set; } = true;
}

public class SchoolContext : DbContext
{
    public DbSet<Schedule>          Schedules          { get; set; } = null!;
    public DbSet<User>              Users              { get; set; } = null!;
    public DbSet<Teacher>           Teachers           { get; set; } = null!;
    public DbSet<TeacherSubject>    TeacherSubjects    { get; set; } = null!;
    public DbSet<TeacherAttendance> TeacherAttendance  { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder o)
        => o.UseSqlite("Data Source=school.db");

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Schedule>().ToTable("Schedule");
        m.Entity<User>().ToTable("Users");
        m.Entity<Teacher>().ToTable("Teachers");
        m.Entity<TeacherSubject>().ToTable("TeacherSubjects");
        m.Entity<TeacherAttendance>().ToTable("TeacherAttendance");
    }
}
