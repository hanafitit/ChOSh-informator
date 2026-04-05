using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.EntityFrameworkCore;
Console.OutputEncoding = Encoding.UTF8;
using var cts = new CancellationTokenSource();
var userStates = new Dictionary<long, string>();
var userNames  = new Dictionary<long, string>();
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
    var bot = new TelegramBotClient(token);
    Console.WriteLine("BOT STARTING...");

    // Сбрасываем webhook и удаляем застрявшие обновления
    await bot.DeleteWebhook(dropPendingUpdates: true);
    Console.WriteLine("Webhook сброшен.");

    // Небольшая пауза, чтобы старый экземпляр успел завершиться
    await Task.Delay(3000);

    bot.StartReceiving(
        updateHandler: HandleUpdate,
        errorHandler: HandleError,
        receiverOptions: new ReceiverOptions { AllowedUpdates = [] },
        cancellationToken: cts.Token
    );
    var me = await bot.GetMe();
    Console.WriteLine($"Бот запущен: @{me.Username}");
    _ = RunWebServer(cts.Token);
    Console.WriteLine("Для остановки нажмите Ctrl+C.");
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Бот остановлен.");
}
catch (Exception ex)
{
    Console.WriteLine($"Критическая ошибка: {ex.Message}");
    Environment.Exit(1);
}
// ══════════════════════════════════════════════
// ОБРАБОТЧИК СООБЩЕНИЙ
// ═════════════════════════════════════v═════════
async Task HandleUpdate(ITelegramBotClient botClient, Update update, CancellationToken ct)
{
    string adminId = Environment.GetEnvironmentVariable("ADMIN_Id")
        ?? throw new Exception("ADMIN_BOT не задан!");
    if (update.Message?.Text is { } text)
    {
        var chatId = update.Message.Chat.Id;
        switch (text)
        {
            case "/start":
            {
                User? found = null;
                using var db = new SchoolContext();
                var all = db.Users.ToList();
                foreach (var u in all)
                {
                    if (u.TelegramId == chatId)
                    {
                        found = u;
                        break;
                    }
                }
                if (found != null)
                {
                    var keyboard = new ReplyKeyboardMarkup(new[]
                    {
                        new[] { new KeyboardButton("📅 Расписание"), new KeyboardButton("📢 Объявления") },
                        new[] { new KeyboardButton("📊 Опросы"),     new KeyboardButton("👤 Профиль") }
                    })
                    { ResizeKeyboard = true };
                    await botClient.SendMessage(chatId, "Главное меню:", replyMarkup: keyboard, cancellationToken: ct);
                }
                else
                {
                    await botClient.SendMessage(chatId, "Добро пожаловать! Для начала зарегистрируйся.\nВведи своё имя:", cancellationToken: ct);
                    userStates[chatId] = "waitingName";
                }
                break;
            }
            case "📅 Расписание":
            {
                var scheduleKeyboard = new ReplyKeyboardMarkup(new[]
                {
                    new[] { new KeyboardButton("Сегодня"), new KeyboardButton("Завтра") },
                    new[] { new KeyboardButton("Неделя") },
                    new[] { new KeyboardButton("⬅️ Назад") }
                })
                { ResizeKeyboard = true };
                await botClient.SendMessage(chatId, "Выбери вариант:", replyMarkup: scheduleKeyboard, cancellationToken: ct);
                break;
            }
            case "/admin":
                {
                    if (chatId.ToString() == adminId)
                    {
                        break;
                    }
                    else
                    {
                        await botClient.SendMessage(chatId, "У вас нет прав", cancellationToken: ct);
                        break;
                    }
                }
            case "Сегодня":
                {
                    using var db = new SchoolContext();
                    // Получаем класс текущего пользователя
                    var user = db.Users.FirstOrDefault(u => u.TelegramId == chatId);
                    if (user != null)
                    {
                        string today = DateTime.Now.DayOfWeek.ToString();
                        // Получаем все уроки для этого класса на текущий день
                        var scheduleForToday = db.Schedules
                            .Where(s => s.ClassName == user.ClassName && s.DayOfWeek == today)
                            .OrderBy(s => s.LessonNumber)
                            .ToList();
                        if (scheduleForToday.Any())
                        {
                            var sb = new StringBuilder($"Расписание на сегодня ({today}):\n");
                            foreach (var item in scheduleForToday)
                            {
                                sb.AppendLine($"{item.LessonNumber}: {item.Subject} ({item.StartTime} - {item.EndTime})");
                            }
                            await botClient.SendMessage(chatId, sb.ToString(), cancellationToken: ct);
                        }
                        else
                        {
                            await botClient.SendMessage(chatId, "У вас нет уроков на сегодня.", cancellationToken: ct);
                        }
                    }
                    else
                    {
                        await botClient.SendMessage(chatId, "Вы не зарегистрированы! Пожалуйста, зарегистрируйтесь.", cancellationToken: ct);
                    }
                    break;
                }
            case "📊 Опросы":
                await botClient.SendMessage(chatId, "Здесь будут опросы", cancellationToken: ct);
                break;
            case "📢 Объявления":
                await botClient.SendMessage(chatId, "Здесь будут объявления", cancellationToken: ct);
                break;
            case "👤 Профиль":
                await botClient.SendMessage(chatId, $"Твой ID: {chatId}", cancellationToken: ct);
                break;
            case "⬅️ Назад":
            {
                var mainKeyboard = new ReplyKeyboardMarkup(new[]
                {
                    new[] { new KeyboardButton("📅 Расписание"), new KeyboardButton("📢 Объявления") },
                    new[] { new KeyboardButton("📊 Опросы"),     new KeyboardButton("👤 Профиль") }
                })
                { ResizeKeyboard = true };
                await botClient.SendMessage(chatId, "Главное меню:", replyMarkup: mainKeyboard, cancellationToken: ct);
                break;
            }
            default:
            {
                if (userStates.ContainsKey(chatId) && userStates[chatId] == "waitingName")
                {
                    userNames[chatId] = text;
                    userStates[chatId] = "waitingClass";
                    await botClient.SendMessage(chatId, "Теперь введи свой класс (например: 10 класс):", cancellationToken: ct);
                }
                else if (userStates.ContainsKey(chatId) && userStates[chatId] == "waitingClass")
                {
                    string name      = userNames[chatId];
                    string className = text;
                    using var db = new SchoolContext();
                    db.Users.Add(new User
                    {
                        TelegramId = chatId,
                        FirstName  = name,
                        ClassName  = className,
                        Role       = "student"
                    });
                    db.SaveChanges();
                    userStates.Remove(chatId);
                    userNames.Remove(chatId);
                    var keyboard = new ReplyKeyboardMarkup(new[]
                    {
                        new[] { new KeyboardButton("📅 Расписание"), new KeyboardButton("📢 Объявления") },
                        new[] { new KeyboardButton("📊 Опросы"),     new KeyboardButton("👤 Профиль") }
                    })
                    { ResizeKeyboard = true };
                    await botClient.SendMessage(chatId, $"Отлично, {name}! Регистрация завершена.", replyMarkup: keyboard, cancellationToken: ct);
                }
                else
                {
                    await botClient.SendMessage(chatId, "Используйте кнопки, чтобы управлять ботом", cancellationToken: ct);
                }
                break;
            }
        }
    }
    if (update.CallbackQuery is { } query)
    {
        await botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);
    }
}
// ══════════════════════════════════════════════
// ОБРАБОТЧИК ОШИБОК
// ══════════════════════════════════════════════
Task HandleError(ITelegramBotClient botClient, Exception exception, CancellationToken ct)
{
    Console.WriteLine($"Ошибка: {exception.Message}");
    if (exception.Message.Contains("Conflict"))
        Thread.Sleep(5000); // подождать, пока старый экземпляр умрёт
    return Task.CompletedTask;
}
// ══════════════════════════════════════════════
// ВЕБ-СЕРВЕР ДЛЯ RENDER
// ══════════════════════════════════════════════
static async Task RunWebServer(CancellationToken ct)
{
    var port     = Environment.GetEnvironmentVariable("PORT") ?? "8080";
    var listener = new System.Net.HttpListener();
    listener.Prefixes.Add($"http://+:{port}/");
    listener.Start();
    Console.WriteLine($"Веб-сервер запущен на порту {port}.");
    while (!ct.IsCancellationRequested)
    {
        try
        {
            var context  = await listener.GetContextAsync();
            var response = context.Response;
            var body     = Encoding.UTF8.GetBytes("OK");
            response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body, ct);
            response.OutputStream.Close();
        }
        catch (OperationCanceledException) { break; }
        catch { }
    }
    listener.Stop();
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
}
public class User
{
    public int    Id         { get; set; }
    public long   TelegramId { get; set; }
    public string FirstName  { get; set; } = "";
    public string ClassName  { get; set; } = "";
    public string Role       { get; set; } = "student";
}
public class SchoolContext : DbContext
{
    public DbSet<Schedule> Schedules { get; set; } = null!;
    public DbSet<User>     Users     { get; set; } = null!;
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.UseSqlite("Data Source=school.db");
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Schedule>().ToTable("Schedule");
        modelBuilder.Entity<User>().ToTable("Users");
    }
}

