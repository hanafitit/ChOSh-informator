using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using ЧОШ_информатор.Data;
using ЧОШ_информатор.Models;
using ЧОШ_информатор.Services;
using ЧОШ_информатор.Helpers;
using ЧОШ_информатор.Constants;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace ЧОШ_информатор.Handlers;

public class BotHandler
{
    private readonly ITelegramBotClient _botClient;
    private readonly GitHubBackupService _backupService;
    private readonly ILogger<BotHandler> _logger;
    private readonly string _adminId;
    private readonly ConcurrentDictionary<long, UserSession> _userSessions = new();

    public BotHandler(ITelegramBotClient botClient, GitHubBackupService backupService, ILogger<BotHandler> logger)
    {
        _botClient = botClient;
        _backupService = backupService;
        _logger = logger;
        _adminId = Environment.GetEnvironmentVariable("ADMIN_Id")
            ?? throw new Exception("ADMIN_Id не задан!");
    }

    public async Task HandleUpdateAsync(Update update, CancellationToken ct)
    {
        var msg = update.Message;
        var chatId = msg?.Chat.Id ?? update.CallbackQuery?.Message?.Chat.Id ?? 0;
        if (chatId == 0) return;

        bool isAdmin = chatId.ToString() == _adminId;

        string? text = msg?.Text ?? msg?.Caption;
        string? photoId = msg?.Photo?.LastOrDefault()?.FileId;
        string? username = msg?.From?.Username ?? update.CallbackQuery?.From?.Username;

        using var db = new SchoolContext();
        var teacher = db.Teachers.FirstOrDefault(t => t.TelegramId == chatId);
        if (teacher == null && !string.IsNullOrEmpty(username))
        {
            teacher = db.Teachers.FirstOrDefault(t => t.Username.ToLower() == username.ToLower() || t.Username.ToLower() == "@" + username.ToLower());
            if (teacher != null && teacher.TelegramId == 0)
            {
                teacher.TelegramId = chatId;
                db.SaveChanges();
                _logger.LogInformation("Updated TelegramId for teacher {TeacherName} using username @{Username}", teacher.Name, username);
            }
        }
        bool isTeacher = teacher != null;

        var session = _userSessions.GetOrAdd(chatId, _ => new UserSession());

        try
        {
            // ══════════════════════════════════════════════════════════════════════
            // МНОГОШАГОВЫЕ СОСТОЯНИЯ
            // ══════════════════════════════════════════════════════════════════════
            if (!string.IsNullOrEmpty(session.State))
            {
                if (await HandleStateAsync(chatId, session, text, photoId, isAdmin, isTeacher, teacher, db, ct))
                    return;
            }

            if (text == null) return;

        // ══════════════════════════════════════════════════════════════════════
        // SWITCH
        // ══════════════════════════════════════════════════════════════════════
        switch (text)
        {
            case "/start":
                await HandleStartAsync(chatId, isAdmin, isTeacher, teacher, db, ct);
                break;

            case "/help":
                await HandleHelpAsync(chatId, isAdmin, isTeacher, ct);
                break;

            case "/admin":
                if (isAdmin)
                    await _botClient.SendMessage(chatId, Messages.AdminPanel,
                        parseMode: ParseMode.Html, replyMarkup: KeyboardHelper.AdminKeyboard(), cancellationToken: ct);
                else
                    await _botClient.SendMessage(chatId, Messages.NoRights, parseMode: ParseMode.Html, cancellationToken: ct);
                break;

            case "/backup":
                if (isAdmin)
                {
                    await _botClient.SendMessage(chatId, "⏳ Сохраняю БД...", cancellationToken: ct);
                    await _backupService.BackupAsync();
                    await _botClient.SendMessage(chatId, "✅ БД сохранена в GitHub!", cancellationToken: ct);
                }
                else
                    await _botClient.SendMessage(chatId, "У вас нет прав", cancellationToken: ct);
                break;

            case "📢 Объявление":
                if (!isAdmin) break;
                session.State = "waitingAnnouncement";
                await _botClient.SendMessage(chatId,
                    "📝 Отправь объявление — текст, фото или фото с подписью.",
                    replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
                break;

            case "👨‍🏫 Учителя":
                if (!isAdmin) break;
                var teacherKb = new ReplyKeyboardMarkup(new[]
                {
                    new[] { new KeyboardButton("➕ Добавить учителя"), new KeyboardButton("➖ Удалить учителя") },
                    new[] { new KeyboardButton("📋 Список учителей") },
                    new[] { new KeyboardButton("⬅️ Назад") }
                })
                { ResizeKeyboard = true };
                await _botClient.SendMessage(chatId, "Управление учителями:", replyMarkup: teacherKb, cancellationToken: ct);
                break;

            case "➕ Добавить учителя":
                if (!isAdmin) break;
                session.State = "teacher_add_name";
                await _botClient.SendMessage(chatId, "Введи имя учителя:",
                    replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
                break;

            case "➖ Удалить учителя":
                if (!isAdmin) break;
                var teachers = db.Teachers.ToList();
                if (!teachers.Any()) { await _botClient.SendMessage(chatId, "Учителей нет.", cancellationToken: ct); break; }
                var rows = teachers.Select(t => new[] { new KeyboardButton(t.Name) }).ToArray();
                session.State = "teacher_delete";
                await _botClient.SendMessage(chatId, "Выбери учителя для удаления:",
                    replyMarkup: new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true }, cancellationToken: ct);
                break;

            case "📋 Список учителей":
                if (!isAdmin) break;
                await HandleTeacherListAsync(chatId, db, ct);
                break;

            case "📋 Проверить расписание":
                if (!isAdmin) break;
                await HandleCheckScheduleAsync(chatId, db, ct);
                break;

            case "✅ Подтвердить и разослать":
                if (!isAdmin) break;
                await HandleConfirmAndSendAsync(chatId, db, ct);
                break;

            case "✅ Буду на уроках":
            case "❌ Меня не будет":
                if (!isTeacher) break;
                await HandleTeacherAttendanceAsync(chatId, teacher!, text == "✅ Буду на уроках", db, ct);
                break;

            case "🗓 Моё расписание":
                if (!isTeacher) break;
                await HandleMyScheduleAsync(chatId, teacher!, db, ct);
                break;

            case "🏫 Расписание класса":
                if (!isTeacher || !teacher!.IsHomeroom) break;
                await HandleClassScheduleAsync(chatId, teacher.HomeroomClass, db, ct);
                break;

            case "📅 Расписание":
                var kb = new ReplyKeyboardMarkup(new[]
                {
                    new[] { new KeyboardButton("Сегодня"), new KeyboardButton("Завтра") },
                    new[] { new KeyboardButton("Неделя") },
                    new[] { new KeyboardButton("⬅️ Назад") }
                })
                { ResizeKeyboard = true };
                await _botClient.SendMessage(chatId, "Выбери вариант:", replyMarkup: kb, cancellationToken: ct);
                break;

            case "Сегодня":
                await HandleStudentScheduleAsync(chatId, "Сегодня", db, ct);
                break;

            case "Завтра":
                await HandleStudentScheduleAsync(chatId, "Завтра", db, ct);
                break;

            case "Неделя":
                await HandleStudentScheduleAsync(chatId, "Неделя", db, ct);
                break;

            case "📊 Упр. опросами":
                if (!isAdmin) break;
                await _botClient.SendMessage(chatId, "Управление опросами:", replyMarkup: KeyboardHelper.AdminPollKeyboard(), cancellationToken: ct);
                break;

            case "➕ Создать опрос":
                if (!isAdmin) break;
                session.State = "poll_create_question";
                await _botClient.SendMessage(chatId, "Введите вопрос для опроса:", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
                break;

            case "🛑 Остановить опрос":
                if (!isAdmin) break;
                await HandleStopPollListAsync(chatId, db, ct);
                break;

            case "📊 Опросы":
                await HandlePollListAsync(chatId, db, ct);
                break;

            case "👤 Профиль":
                await HandleProfileAsync(chatId, isTeacher, teacher, db, ct);
                break;

            case "⬅️ Назад":
                if (isAdmin)
                    await _botClient.SendMessage(chatId, "Главное меню:", replyMarkup: KeyboardHelper.AdminKeyboard(), cancellationToken: ct);
                else if (isTeacher)
                    await _botClient.SendMessage(chatId, "Главное меню:", replyMarkup: KeyboardHelper.TeacherKeyboard(teacher!), cancellationToken: ct);
                else
                    await _botClient.SendMessage(chatId, "Главное меню:", replyMarkup: KeyboardHelper.MainKeyboard(), cancellationToken: ct);
                break;

            default:
                await _botClient.SendMessage(chatId, "Используйте кнопки, чтобы управлять ботом", cancellationToken: ct);
                break;
        }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling update for chatId {ChatId}", chatId);
            await _botClient.SendMessage(chatId, Messages.ErrorOccurred, parseMode: ParseMode.Html, cancellationToken: ct);
        }

        if (update.CallbackQuery is { } query)
            await _botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);
    }

    private async Task<bool> HandleStateAsync(long chatId, UserSession session, string? text, string? photoId, bool isAdmin, bool isTeacher, Teacher? teacher, SchoolContext db, CancellationToken ct)
    {
        if (session.State.StartsWith("poll_vote_"))
        {
            if (int.TryParse(session.State.Replace("poll_vote_", ""), out int pollId) && int.TryParse(text, out int optionIndex))
            {
                await HandleVoteAsync(chatId, pollId, optionIndex, db, ct);
                session.Reset();
                return true;
            }
        }

        if (session.State == "poll_stop" && isAdmin)
        {
            if (int.TryParse(text, out int pollId))
            {
                var poll = db.Polls.FirstOrDefault(p => p.Id == pollId);
                if (poll != null)
                {
                    poll.IsActive = false;
                    db.SaveChanges();
                    await _botClient.SendMessage(chatId, $"✅ Опрос \"{poll.Question}\" остановлен.", replyMarkup: KeyboardHelper.AdminPollKeyboard(), cancellationToken: ct);
                }
                session.Reset();
                return true;
            }
        }

        if (isAdmin)
        {
            if (session.State == "poll_create_question")
            {
                if (text == null) return true;
                session.TempData["question"] = text;
                session.State = "poll_create_options";
                await _botClient.SendMessage(chatId, "Введите варианты ответа через черту (например: Да|Нет|Не знаю):", cancellationToken: ct);
                return true;
            }
            if (session.State == "poll_create_options")
            {
                if (text == null) return true;
                var question = session.TempData["question"];
                var options = text.Split('|', StringSplitOptions.RemoveEmptyEntries);
                if (options.Length < 2)
                {
                    await _botClient.SendMessage(chatId, "Нужно минимум 2 варианта ответа.", cancellationToken: ct);
                    return true;
                }

                var poll = new ЧОШ_информатор.Models.Poll { Question = question, CreatedAt = TimeHelper.NowKz().ToString("yyyy-MM-dd HH:mm") };
                db.Polls.Add(poll);
                db.SaveChanges();

                foreach (var opt in options)
                {
                    db.PollOptions.Add(new ЧОШ_информатор.Models.PollOption { PollId = poll.Id, Text = opt.Trim() });
                }
                db.SaveChanges();

                session.Reset();
                await _botClient.SendMessage(chatId, "✅ Опрос создан и запущен!", replyMarkup: KeyboardHelper.AdminPollKeyboard(), cancellationToken: ct);
                return true;
            }
        }

        // ── Объявление ────────────────────────────────────────────────────
        if (session.State == "waitingAnnouncement" && isAdmin)
        {
            if (text == null && photoId == null)
            {
                await _botClient.SendMessage(chatId, "Отправь текст или фото (можно с подписью).", cancellationToken: ct);
                return true;
            }
            session.Reset();

            var allUsers = db.Users.ToList();
            int sent = 0, failed = 0;
            foreach (var u in allUsers)
            {
                try
                {
                    if (photoId != null)
                        await _botClient.SendPhoto(u.TelegramId, InputFile.FromFileId(photoId),
                            caption: text, cancellationToken: ct);
                    else
                        await _botClient.SendMessage(u.TelegramId, text!, cancellationToken: ct);
                    sent++;
                }
                catch { failed++; }
            }
            await _botClient.SendMessage(chatId,
                $"✅ Объявление отправлено!\n👤 Доставлено: {sent}\n❌ Ошибок: {failed}",
                replyMarkup: KeyboardHelper.AdminKeyboard(), cancellationToken: ct);
            return true;
        }

        // ── Добавление учителя ───────────────────────────────────────────
        if (isAdmin)
        {
            if (session.State == "teacher_add_name")
            {
                if (text == null) return true;
                session.TempData["name"] = text;
                session.State = "teacher_add_id";
                await _botClient.SendMessage(chatId, "Введи Telegram ID учителя:",
                    replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
                return true;
            }
            if (session.State == "teacher_add_id")
            {
                if (text == null) return true;
                string input = text.Trim();
                if (long.TryParse(input, out _))
                {
                    session.TempData["telegramId"] = input;
                    session.TempData["username"] = "";
                }
                else if (input.StartsWith("@") && input.Length > 1)
                {
                    session.TempData["telegramId"] = "0";
                    session.TempData["username"] = input;
                }
                else
                {
                    await _botClient.SendMessage(chatId, "Неверный формат. Введи числовой Telegram ID или @username:", cancellationToken: ct);
                    return true;
                }
                session.State = "teacher_add_homeroom";

                var classesKb = new ReplyKeyboardMarkup(new[]
                {
                    new[] { new KeyboardButton("5-6 класс"), new KeyboardButton("7"), new KeyboardButton("8") },
                    new[] { new KeyboardButton("9"), new KeyboardButton("10"), new KeyboardButton("11") },
                    new[] { new KeyboardButton("Нет") }
                })
                { ResizeKeyboard = true };
                await _botClient.SendMessage(chatId, "Классный руководитель какого класса? (или «Нет»):",
                    replyMarkup: classesKb, cancellationToken: ct);
                return true;
            }
            if (session.State == "teacher_add_homeroom")
            {
                if (text == null) return true;
                session.TempData["homeroom"] = text == "Нет" ? "" : text;
                session.State = "teacher_add_subjects";
                await _botClient.SendMessage(chatId,
                    "Введи предметы и классы которые ведёт учитель.\n" +
                    "Формат — каждый предмет с новой строки:\n" +
                    "<b>математика|10</b>\n<b>математика|11</b>\n<b>география|5-6 класс</b>\n\n" +
                    "Отправь всё одним сообщением.",
                    parseMode: ParseMode.Html,
                    replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
                return true;
            }
            if (session.State == "teacher_add_subjects")
            {
                if (text == null) return true;
                long tid = long.Parse(session.TempData["telegramId"]);
                string tusername = session.TempData["username"];
                string tname = session.TempData["name"];
                string homeroom = session.TempData["homeroom"];

                if (tid != 0)
                {
                    var existing = db.Teachers.FirstOrDefault(t => t.TelegramId == tid);
                    if (existing != null)
                    {
                        await _botClient.SendMessage(chatId, "⚠️ Учитель с таким ID уже существует.",
                            replyMarkup: KeyboardHelper.AdminKeyboard(), cancellationToken: ct);
                        session.Reset();
                        return true;
                    }
                }
                else if (!string.IsNullOrEmpty(tusername))
                {
                    var existing = db.Teachers.FirstOrDefault(t => t.Username.ToLower() == tusername.ToLower());
                    if (existing != null)
                    {
                        await _botClient.SendMessage(chatId, "⚠️ Учитель с таким Username уже существует.",
                            replyMarkup: KeyboardHelper.AdminKeyboard(), cancellationToken: ct);
                        session.Reset();
                        return true;
                    }
                }

                db.Teachers.Add(new Teacher
                {
                    TelegramId = tid,
                    Username = tusername,
                    Name = tname,
                    IsHomeroom = !string.IsNullOrEmpty(homeroom),
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
                            Subject = parts[0].Trim(),
                            ClassName = parts[1].Trim()
                        });
                        added++;
                    }
                }

                db.SaveChanges();
                session.Reset();

                await _botClient.SendMessage(chatId,
                $"✅ Учитель <b>{tname}</b> добавлен!\n👤 ID/User: {(tid != 0 ? tid.ToString() : tusername)}\n📚 Предметов: {added}\n🏫 Класс рук.: {(string.IsNullOrEmpty(homeroom) ? "нет" : homeroom)}",
                    parseMode: ParseMode.Html, replyMarkup: KeyboardHelper.AdminKeyboard(), cancellationToken: ct);
                return true;
            }
            if (session.State == "teacher_delete")
            {
                if (text == null) return true;
                var toDelete = db.Teachers.FirstOrDefault(t => t.Name == text);
                if (toDelete == null)
                    await _botClient.SendMessage(chatId, "Учитель не найден.", replyMarkup: KeyboardHelper.AdminKeyboard(), cancellationToken: ct);
                else
                {
                    db.TeacherSubjects.RemoveRange(db.TeacherSubjects.Where(s => s.TeacherName == text));
                    db.Teachers.Remove(toDelete);
                    db.SaveChanges();
                    await _botClient.SendMessage(chatId, $"✅ Учитель {text} удалён.", replyMarkup: KeyboardHelper.AdminKeyboard(), cancellationToken: ct);
                }
                session.Reset();
                return true;
            }
        }

        // ── Регистрация ───────────────────────────────────────────────────
        if (session.State == "waitingName" && text != null)
        {
            session.Name = text;
            session.State = "waitingClass";
            var classKb = new ReplyKeyboardMarkup(new[]
            {
                new[] { new KeyboardButton("5-6 класс") },
                new[] { new KeyboardButton("7"), new KeyboardButton("8") },
                new[] { new KeyboardButton("9"), new KeyboardButton("10"), new KeyboardButton("11") }
            })
            { ResizeKeyboard = true };
            await _botClient.SendMessage(chatId, $"Отлично, {text}! 👇 Теперь выбери свой класс:",
                replyMarkup: classKb, cancellationToken: ct);
            return true;
        }
        if (session.State == "waitingClass" && text != null
            && new[] { "5-6 класс", "7", "8", "9", "10", "11" }.Contains(text))
        {
            string name = session.Name;
            string className = text;
            db.Users.Add(new ЧОШ_информатор.Models.User { TelegramId = chatId, FirstName = name, ClassName = className, Role = "student" });
            db.SaveChanges();
            session.Reset();
            string display = className == "5-6 класс" ? "5-6" : className;
            await _botClient.SendMessage(chatId,
                $"✅ Готово, {name}! Ты зарегистрирован в {display} классе.",
                replyMarkup: KeyboardHelper.MainKeyboard(), cancellationToken: ct);
            return true;
        }

        return false;
    }

    private async Task HandleStartAsync(long chatId, bool isAdmin, bool isTeacher, Teacher? teacher, SchoolContext db, CancellationToken ct)
    {
        if (isAdmin)
        {
            await _botClient.SendMessage(chatId, Messages.WelcomeAdmin,
                parseMode: ParseMode.Html, replyMarkup: KeyboardHelper.AdminKeyboard(), cancellationToken: ct);
            return;
        }
        if (isTeacher)
        {
            await _botClient.SendMessage(chatId, string.Format(Messages.WelcomeTeacher, teacher!.Name),
                parseMode: ParseMode.Html, replyMarkup: KeyboardHelper.TeacherKeyboard(teacher), cancellationToken: ct);
            return;
        }
        var found = db.Users.FirstOrDefault(u => u.TelegramId == chatId);
        if (found != null)
            await _botClient.SendMessage(chatId, string.Format(Messages.WelcomeStudent, found.FirstName),
                parseMode: ParseMode.Html, replyMarkup: KeyboardHelper.MainKeyboard(), cancellationToken: ct);
        else
        {
            await _botClient.SendMessage(chatId, Messages.RegistrationPrompt,
                parseMode: ParseMode.Html, replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
            _userSessions[chatId].State = "waitingName";
        }
    }

    private async Task HandleHelpAsync(long chatId, bool isAdmin, bool isTeacher, CancellationToken ct)
    {
        string helpMsg = Messages.HelpStudent;
        if (isAdmin) helpMsg = Messages.HelpAdmin;
        else if (isTeacher) helpMsg = Messages.HelpTeacher;

        await _botClient.SendMessage(chatId, helpMsg, parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleTeacherListAsync(long chatId, SchoolContext db, CancellationToken ct)
    {
        var teachers = db.Teachers.ToList();
        if (!teachers.Any()) { await _botClient.SendMessage(chatId, "Учителей пока нет.", cancellationToken: ct); return; }
        var sb = new StringBuilder("👨‍🏫 Учителя:\n\n");
        foreach (var t in teachers)
        {
            sb.AppendLine($"• {t.Name} ({(t.TelegramId != 0 ? $"ID: {t.TelegramId}" : $"User: {t.Username}")})");
            if (t.IsHomeroom) sb.AppendLine($"  🏫 Классный рук.: {t.HomeroomClass}");
            foreach (var s in db.TeacherSubjects.Where(s => s.TeacherName == t.Name).ToList())
                sb.AppendLine($"  📚 {s.Subject} — {s.ClassName}");
            sb.AppendLine();
        }
        await _botClient.SendMessage(chatId, sb.ToString(), cancellationToken: ct);
    }

    private async Task HandleCheckScheduleAsync(long chatId, SchoolContext db, CancellationToken ct)
    {
        string today = TimeHelper.NowKz().DayOfWeek.ToString();
        string dateStr = TimeHelper.NowKz().ToString("yyyy-MM-dd");

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
                string end = slot?.EndTime ?? remaining[i].EndTime;
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

        await _botClient.SendMessage(chatId, sb.ToString(), replyMarkup: confirmKb, cancellationToken: ct);
    }

    private async Task HandleConfirmAndSendAsync(long chatId, SchoolContext db, CancellationToken ct)
    {
        string today = TimeHelper.NowKz().DayOfWeek.ToString();
        string dateStr = TimeHelper.NowKz().ToString("yyyy-MM-dd");

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
                    var slot = timeSlots.ElementAtOrDefault(i);
                    string start = slot?.StartTime ?? remaining[i].StartTime;
                    string end = slot?.EndTime ?? remaining[i].EndTime;
                    sb.AppendLine($"{i + 1}: {remaining[i].Subject} ({start} - {end})");
                }

                await _botClient.SendMessage(student.TelegramId, sb.ToString(), cancellationToken: ct);
                sent++;
            }
            catch { failed++; }
        }

        await _botClient.SendMessage(chatId,
            $"✅ Расписание разослано!\n👤 Доставлено: {sent}\n❌ Ошибок: {failed}",
            replyMarkup: KeyboardHelper.AdminKeyboard(), cancellationToken: ct);
    }

    private async Task HandleTeacherAttendanceAsync(long chatId, Teacher teacher, bool isPresent, SchoolContext db, CancellationToken ct)
    {
        string dateStr = TimeHelper.NowKz().ToString("yyyy-MM-dd");
        var existing = db.TeacherAttendance.FirstOrDefault(a => a.Date == dateStr && a.TeacherName == teacher.Name);
        if (existing != null)
            existing.IsPresent = isPresent;
        else
            db.TeacherAttendance.Add(new TeacherAttendance { TeacherName = teacher.Name, Date = dateStr, IsPresent = isPresent });
        db.SaveChanges();

        string reply = isPresent ? "✅ Отмечено: вы будете на уроках." : "❌ Отмечено: вас не будет. Администратор проверит расписание.";
        await _botClient.SendMessage(chatId, reply, replyMarkup: KeyboardHelper.TeacherKeyboard(teacher), cancellationToken: ct);
    }

    private async Task HandleMyScheduleAsync(long chatId, Teacher teacher, SchoolContext db, CancellationToken ct)
    {
        string today = TimeHelper.NowKz().DayOfWeek.ToString();
        var subjects = db.TeacherSubjects.Where(s => s.TeacherName == teacher.Name).ToList();
        var myLessons = db.Schedules.Where(s => s.DayOfWeek == today).ToList()
            .Where(s => subjects.Any(sub => sub.Subject == s.Subject && sub.ClassName == s.ClassName))
            .OrderBy(s => s.LessonNumber).ToList();

        if (!myLessons.Any()) { await _botClient.SendMessage(chatId, "У вас нет уроков сегодня.", cancellationToken: ct); return; }

        var sb = new StringBuilder($"📅 Ваше расписание на сегодня ({today}):\n\n");
        foreach (var l in myLessons)
            sb.AppendLine($"{l.LessonNumber}: {l.Subject} ({l.ClassName}) {l.StartTime}–{l.EndTime}");
        await _botClient.SendMessage(chatId, sb.ToString(), cancellationToken: ct);
    }

    private async Task HandleClassScheduleAsync(long chatId, string className, SchoolContext db, CancellationToken ct)
    {
        string today = TimeHelper.NowKz().DayOfWeek.ToString();
        var lessons = db.Schedules.Where(s => s.ClassName == className && s.DayOfWeek == today).OrderBy(s => s.LessonNumber).ToList();
        if (!lessons.Any()) { await _botClient.SendMessage(chatId, "Уроков нет.", cancellationToken: ct); return; }

        var sb = new StringBuilder($"📅 Расписание {className} на сегодня ({today}):\n\n");
        foreach (var l in lessons)
            sb.AppendLine($"{l.LessonNumber}: {l.Subject} ({l.StartTime}–{l.EndTime})");
        await _botClient.SendMessage(chatId, sb.ToString(), cancellationToken: ct);
    }

    private async Task HandleStudentScheduleAsync(long chatId, string type, SchoolContext db, CancellationToken ct)
    {
        var user = db.Users.FirstOrDefault(u => u.TelegramId == chatId);
        if (user == null) { await _botClient.SendMessage(chatId, "Вы не зарегистрированы!", cancellationToken: ct); return; }

        if (type == "Сегодня")
        {
            string today = TimeHelper.NowKz().DayOfWeek.ToString();
            var lessons = db.Schedules.Where(s => s.ClassName == user.ClassName && s.DayOfWeek == today).OrderBy(s => s.LessonNumber).ToList();
            if (!lessons.Any()) { await _botClient.SendMessage(chatId, "У вас нет уроков на сегодня.", cancellationToken: ct); return; }
            var sb = new StringBuilder($"Расписание на сегодня ({today}):\n");
            foreach (var item in lessons) sb.AppendLine($"{item.LessonNumber}: {item.Subject} ({item.StartTime} - {item.EndTime})");
            await _botClient.SendMessage(chatId, sb.ToString(), cancellationToken: ct);
        }
        else if (type == "Завтра")
        {
            string tomorrow = TimeHelper.NowKz().AddDays(1).DayOfWeek.ToString();
            var lessons = db.Schedules.Where(s => s.ClassName == user.ClassName && s.DayOfWeek == tomorrow).OrderBy(s => s.LessonNumber).ToList();
            if (!lessons.Any()) { await _botClient.SendMessage(chatId, "У вас нет уроков завтра.", cancellationToken: ct); return; }
            var sb = new StringBuilder($"Расписание на завтра ({tomorrow}):\n");
            foreach (var item in lessons) sb.AppendLine($"{item.LessonNumber}: {item.Subject} ({item.StartTime} - {item.EndTime})");
            await _botClient.SendMessage(chatId, sb.ToString(), cancellationToken: ct);
        }
        else if (type == "Неделя")
        {
            var dayOrder = new Dictionary<string, int> { ["Monday"] = 1, ["Tuesday"] = 2, ["Wednesday"] = 3, ["Thursday"] = 4, ["Friday"] = 5, ["Saturday"] = 6, ["Sunday"] = 7 };
            var lessons = db.Schedules.Where(s => s.ClassName == user.ClassName).ToList().OrderBy(s => dayOrder.TryGetValue(s.DayOfWeek, out var o) ? o : 99).ThenBy(s => s.LessonNumber).ToList();
            if (!lessons.Any()) { await _botClient.SendMessage(chatId, "Расписание не найдено.", cancellationToken: ct); return; }
            var sb = new StringBuilder("Расписание на неделю:\n");
            string lastDay = "";
            foreach (var item in lessons)
            {
                if (item.DayOfWeek != lastDay) { sb.AppendLine($"\n📅 {item.DayOfWeek}:"); lastDay = item.DayOfWeek; }
                sb.AppendLine($"  {item.LessonNumber}: {item.Subject} ({item.StartTime} - {item.EndTime})");
            }
            await _botClient.SendMessage(chatId, sb.ToString(), cancellationToken: ct);
        }
    }

    private async Task HandleProfileAsync(long chatId, bool isTeacher, Teacher? teacher, SchoolContext db, CancellationToken ct)
    {
        var user = db.Users.FirstOrDefault(u => u.TelegramId == chatId);
        if (user != null)
            await _botClient.SendMessage(chatId, $"👤 Имя: {user.FirstName}\n🏫 Класс: {user.ClassName}\n🆔 ID: {chatId}", cancellationToken: ct);
        else if (isTeacher)
            await _botClient.SendMessage(chatId, $"👤 {teacher!.Name}\n👨‍🏫 Учитель\n{(string.IsNullOrEmpty(teacher.Username) ? "" : $"👤 Username: {teacher.Username}\n")}🆔 ID: {chatId}", cancellationToken: ct);
        else
            await _botClient.SendMessage(chatId, $"🆔 ID: {chatId}", cancellationToken: ct);
    }

    private async Task HandlePollListAsync(long chatId, SchoolContext db, CancellationToken ct)
    {
        var activePolls = db.Polls.Where(p => p.IsActive).ToList();
        if (!activePolls.Any())
        {
            await _botClient.SendMessage(chatId, "Активных опросов сейчас нет.", cancellationToken: ct);
            return;
        }

        foreach (var poll in activePolls)
        {
            var options = db.PollOptions.Where(o => o.PollId == poll.Id).ToList();
            var sb = new StringBuilder($"📊 <b>{poll.Question}</b>\n\n");
            for (int i = 0; i < options.Count; i++)
            {
                sb.AppendLine($"{i + 1}. {options[i].Text} ({options[i].VoteCount} гол.)");
            }

            var hasVoted = db.PollVotes.Any(v => v.PollId == poll.Id && v.UserTelegramId == chatId);
            if (hasVoted)
            {
                sb.AppendLine("\n✅ Вы уже проголосовали.");
                await _botClient.SendMessage(chatId, sb.ToString(), parseMode: ParseMode.Html, cancellationToken: ct);
            }
            else
            {
                var buttons = new List<KeyboardButton[]>();
                for (int i = 0; i < options.Count; i++)
                {
                    buttons.Add(new[] { new KeyboardButton((i + 1).ToString()) });
                }
                var kb = new ReplyKeyboardMarkup(buttons) { ResizeKeyboard = true };
                _userSessions[chatId].State = $"poll_vote_{poll.Id}";
                await _botClient.SendMessage(chatId, sb.ToString() + "\nВыберите номер варианта для голосования:", parseMode: ParseMode.Html, replyMarkup: kb, cancellationToken: ct);
            }
        }
    }

    private async Task HandleVoteAsync(long chatId, int pollId, int optionIndex, SchoolContext db, CancellationToken ct)
    {
        var poll = db.Polls.FirstOrDefault(p => p.Id == pollId && p.IsActive);
        if (poll == null) return;

        var hasVoted = db.PollVotes.Any(v => v.PollId == pollId && v.UserTelegramId == chatId);
        if (hasVoted)
        {
            await _botClient.SendMessage(chatId, "Вы уже голосовали в этом опросе.", replyMarkup: KeyboardHelper.MainKeyboard(), cancellationToken: ct);
            return;
        }

        var options = db.PollOptions.Where(o => o.PollId == pollId).ToList();
        if (optionIndex < 1 || optionIndex > options.Count)
        {
            await _botClient.SendMessage(chatId, "Неверный вариант.", replyMarkup: KeyboardHelper.MainKeyboard(), cancellationToken: ct);
            return;
        }

        var selectedOption = options[optionIndex - 1];
        selectedOption.VoteCount++;
        db.PollVotes.Add(new PollVote { PollId = pollId, UserTelegramId = chatId, OptionId = selectedOption.Id });
        db.SaveChanges();

        await _botClient.SendMessage(chatId, "✅ Ваш голос учтен!", replyMarkup: KeyboardHelper.MainKeyboard(), cancellationToken: ct);
    }

    private async Task HandleStopPollListAsync(long chatId, SchoolContext db, CancellationToken ct)
    {
        var activePolls = db.Polls.Where(p => p.IsActive).ToList();
        if (!activePolls.Any())
        {
            await _botClient.SendMessage(chatId, "Нет активных опросов.", replyMarkup: KeyboardHelper.AdminPollKeyboard(), cancellationToken: ct);
            return;
        }

        var sb = new StringBuilder("Выберите ID опроса для остановки:\n\n");
        var buttons = new List<KeyboardButton[]>();
        foreach (var poll in activePolls)
        {
            sb.AppendLine($"{poll.Id}: {poll.Question}");
            buttons.Add(new[] { new KeyboardButton(poll.Id.ToString()) });
        }
        var kb = new ReplyKeyboardMarkup(buttons) { ResizeKeyboard = true };
        _userSessions[chatId].State = "poll_stop";
        await _botClient.SendMessage(chatId, sb.ToString(), replyMarkup: kb, cancellationToken: ct);
    }
}
