using System;
using System.Collections.Generic;
using System.IO;
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

    // GitHub backup
    var backup = new GitHubBackup(
        owner: Environment.GetEnvironmentVariable("GH_OWNER") ?? throw new Exception("GH_OWNER не задан"),
        repo:  Environment.GetEnvironmentVariable("GH_REPO")  ?? throw new Exception("GH_REPO не задан"),
        ghToken: Environment.GetEnvironmentVariable("GH_TOKEN") ?? throw new Exception("GH_TOKEN не задан")
    );

    // При старте восстанавливаем БД из GitHub
    await backup.RestoreAsync();

    // Ночной бэкап в 03:00 UTC
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

    Console.WriteLine("BOT STARTING...");

    string webhookUrl = $"{appUrl.TrimEnd('/')}/bot";
    await bot.SetWebhook(webhookUrl, cancellationToken: cts.Token);
    Console.WriteLine($"Webhook установлен: {webhookUrl}");

    var me = await bot.GetMe();
    Console.WriteLine($"Бот запущен: @{me.Username}");

    await RunWebServer(bot, backup, cts.Token);
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
            var req     = context.Request;
            var res     = context.Response;

            if (req.HttpMethod == "GET" && req.Url?.AbsolutePath == "/getdb")
            {
                var key = req.QueryString["key"];
                var secretKey = Environment.GetEnvironmentVariable("DB_KEY");

                if (key != secretKey)
                {
                    res.StatusCode = 403;
                    res.OutputStream.Close();
                    continue;
                }

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
                        var update = JsonSerializer.Deserialize<Update>(json, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
                        if (update != null)
                            await HandleUpdate(bot, backup, update, ct);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Ошибка обработки update: {ex.Message}");
                    }
                }, ct);

                continue;
            }

            res.StatusCode = 404;
            res.OutputStream.Close();
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка веб-сервера: {ex.Message}");
        }
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

    if (update.Message?.Text is { } text)
    {
        var chatId = update.Message.Chat.Id;

        switch (text)
        {
            case "/start":
            {
                User? found = null;
                using var db = new SchoolContext();
                foreach (var u in db.Users.ToList())
                {
                    if (u.TelegramId == chatId) { found = u; break; }
                }

                if (found != null)
                    await botClient.SendMessage(chatId, "Главное меню:", replyMarkup: MainKeyboard(), cancellationToken: ct);
                else
                {
                    await botClient.SendMessage(chatId, "Добро пожаловать! Для начала зарегистрируйся.\nВведи своё имя:", cancellationToken: ct);
                    userStates[chatId] = "waitingName";
                }
                break;
            }

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

            case "/admin":
            {
                if (chatId.ToString() == adminId)
                    await botClient.SendMessage(chatId, "Добро пожаловать, администратор!", cancellationToken: ct);
                else
                    await botClient.SendMessage(chatId, "У вас нет прав", cancellationToken: ct);
                break;
            }

            case "/backup":
            {
                if (chatId.ToString() == adminId)
                {
                    await botClient.SendMessage(chatId, "⏳ Сохраняю БД...", cancellationToken: ct);
                    await backup.BackupAsync();
                    await botClient.SendMessage(chatId, "✅ БД сохранена в GitHub!", cancellationToken: ct);
                }
                else
                    await botClient.SendMessage(chatId, "У вас нет прав", cancellationToken: ct);
                break;
            }

            case "Сегодня":
            {
                using var db = new SchoolContext();
                var user = db.Users.FirstOrDefault(u => u.TelegramId == chatId);
                if (user != null)
                {
                    string today = DateTime.Now.DayOfWeek.ToString();
                    var lessons = db.Schedules
                        .Where(s => s.ClassName == user.ClassName && s.DayOfWeek == today)
                        .OrderBy(s => s.LessonNumber)
                        .ToList();

                    if (lessons.Any())
                    {
                        var sb = new StringBuilder($"Расписание на сегодня ({today}):\n");
                        foreach (var item in lessons)
                            sb.AppendLine($"{item.LessonNumber}: {item.Subject} ({item.StartTime} - {item.EndTime})");
                        await botClient.SendMessage(chatId, sb.ToString(), cancellationToken: ct);
                    }
                    else
                        await botClient.SendMessage(chatId, "У вас нет уроков на сегодня.", cancellationToken: ct);
                }
                else
                    await botClient.SendMessage(chatId, "Вы не зарегистрированы!", cancellationToken: ct);
                break;
            }

            case "Завтра":
            {
                using var db = new SchoolContext();
                var user = db.Users.FirstOrDefault(u => u.TelegramId == chatId);
                if (user != null)
                {
                    string tomorrow = DateTime.Now.AddDays(1).DayOfWeek.ToString();
                    var lessons = db.Schedules
                        .Where(s => s.ClassName == user.ClassName && s.DayOfWeek == tomorrow)
                        .OrderBy(s => s.LessonNumber)
                        .ToList();

                    if (lessons.Any())
                    {
                        var sb = new StringBuilder($"Расписание на завтра ({tomorrow}):\n");
                        foreach (var item in lessons)
                            sb.AppendLine($"{item.LessonNumber}: {item.Subject} ({item.StartTime} - {item.EndTime})");
                        await botClient.SendMessage(chatId, sb.ToString(), cancellationToken: ct);
                    }
                    else
                        await botClient.SendMessage(chatId, "У вас нет уроков завтра.", cancellationToken: ct);
                }
                else
                    await botClient.SendMessage(chatId, "Вы не зарегистрированы!", cancellationToken: ct);
                break;
            }

            case "Неделя":
            {
                using var db = new SchoolContext();
                var user = db.Users.FirstOrDefault(u => u.TelegramId == chatId);
                if (user != null)
                {
                    var lessons = db.Schedules
                        .Where(s => s.ClassName == user.ClassName)
                        .OrderBy(s => s.DayOfWeek)
                        .ThenBy(s => s.LessonNumber)
                        .ToList();

                    if (lessons.Any())
                    {
                        var sb = new StringBuilder("Расписание на неделю:\n");
                        string lastDay = "";
                        foreach (var item in lessons)
                        {
                            if (item.DayOfWeek != lastDay)
                            {
                                sb.AppendLine($"\n📅 {item.DayOfWeek}:");
                                lastDay = item.DayOfWeek;
                            }
                            sb.AppendLine($"  {item.LessonNumber}: {item.Subject} ({item.StartTime} - {item.EndTime})");
                        }
                        await botClient.SendMessage(chatId, sb.ToString(), cancellationToken: ct);
                    }
                    else
                        await botClient.SendMessage(chatId, "Расписание не найдено.", cancellationToken: ct);
                }
                else
                    await botClient.SendMessage(chatId, "Вы не зарегистрированы!", cancellationToken: ct);
                break;
            }

            case "📊 Опросы":
                await botClient.SendMessage(chatId, "Здесь будут опросы", cancellationToken: ct);
                break;

            case "📢 Объявления":
                await botClient.SendMessage(chatId, "Здесь будут объявления", cancellationToken: ct);
                break;

            case "👤 Профиль":
            {
                using var db = new SchoolContext();
                var user = db.Users.FirstOrDefault(u => u.TelegramId == chatId);
                if (user != null)
                    await botClient.SendMessage(chatId, $"👤 Имя: {user.FirstName}\n🏫 Класс: {user.ClassName}\n🆔 ID: {chatId}", cancellationToken: ct);
                else
                    await botClient.SendMessage(chatId, $"🆔 ID: {chatId}", cancellationToken: ct);
                break;
            }

            case "⬅️ Назад":
                await botClient.SendMessage(chatId, "Главное меню:", replyMarkup: MainKeyboard(), cancellationToken: ct);
                break;

            default:
            {
                if (userStates.TryGetValue(chatId, out var state) && state == "waitingName")
                {
                    userNames[chatId]  = text;
                    userStates[chatId] = "waitingClass";
                    await botClient.SendMessage(chatId, "Теперь введи свой класс (например: 10А):", cancellationToken: ct);
                }
                else if (userStates.TryGetValue(chatId, out state) && state == "waitingClass")
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

                    await botClient.SendMessage(chatId, $"Отлично, {name}! Регистрация завершена.", replyMarkup: MainKeyboard(), cancellationToken: ct);
                }
                else
                    await botClient.SendMessage(chatId, "Используйте кнопки, чтобы управлять ботом", cancellationToken: ct);
                break;
            }
        }
    }

    if (update.CallbackQuery is { } query)
        await botClient.AnswerCallbackQuery(query.Id, cancellationToken: ct);
}

// ══════════════════════════════════════════════
// ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ
// ══════════════════════════════════════════════
static ReplyKeyboardMarkup MainKeyboard() =>
    new(new[]
    {
        new[] { new KeyboardButton("📅 Расписание"), new KeyboardButton("📢 Объявления") },
        new[] { new KeyboardButton("📊 Опросы"),     new KeyboardButton("👤 Профиль") }
    })
    { ResizeKeyboard = true };

// ══════════════════════════════════════════════
// GITHUB BACKUP
// ══════════════════════════════════════════════
public class GitHubBackup
{
    private readonly string _owner;
    private readonly string _repo;
    private readonly string _token;
    private const string DbPath   = "school.db";
    private const string FilePath = "backups/school.db";

    public GitHubBackup(string owner, string repo, string ghToken)
    {
        _owner = owner;
        _repo  = repo;
        _token = ghToken;
    }

    public async Task BackupAsync()
    {
        try
        {

            var client = CreateClient();
            byte[] bytes = await File.ReadAllBytesAsync(DbPath);
            Console.WriteLine($"[Backup] Размер файла: {bytes.Length} байт");
            string content = Convert.ToBase64String(bytes);
            Console.WriteLine($"[Backup] Base64 длина: {content.Length}");

            RepositoryContentInfo? existing = null;
            try
            {
                var contents = await client.Repository.Content.GetAllContents(_owner, _repo, FilePath);
                existing = contents[0];
            }
            catch (NotFoundException) { }

            if (existing != null)
                await client.Repository.Content.UpdateFile(_owner, _repo, FilePath,
                    new UpdateFileRequest($"db backup {DateTime.UtcNow:yyyy-MM-dd HH:mm}", content, existing.Sha));
            else
                await client.Repository.Content.CreateFile(_owner, _repo, FilePath,
                    new CreateFileRequest($"db backup {DateTime.UtcNow:yyyy-MM-dd HH:mm}", content));

            Console.WriteLine($"[Backup] БД сохранена в GitHub: {DateTime.UtcNow:HH:mm:ss}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Backup] Ошибка: {ex.Message}");
        }
    }

    public async Task RestoreAsync()
    {
        try
        {
            var client = CreateClient();
            var contents = await client.Repository.Content.GetAllContents(_owner, _repo, FilePath);
            string base64 = contents[0].EncodedContent
                .Replace("\n", "")
                .Replace("\r", "")
                .Replace(" ", "");
            byte[] bytes = Convert.FromBase64String(base64);
            await File.WriteAllBytesAsync(DbPath, bytes);
            Console.WriteLine("[Restore] БД восстановлена из GitHub.");
        }
        catch (NotFoundException)
        {
            Console.WriteLine("[Restore] Бэкапа нет — используется локальная БД из образа.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Restore] Ошибка: {ex.Message}");
        }
    }

    private GitHubClient CreateClient()
    {
        var client = new GitHubClient(new ProductHeaderValue("SchoolBot"));
        client.Credentials = new Credentials(_token);
        return client;
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
