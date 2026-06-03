using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using ЧОШ_информатор.Data;
using ЧОШ_информатор.Services;
using ЧОШ_информатор.Handlers;
using Microsoft.Extensions.Logging;

Console.OutputEncoding = Encoding.UTF8;

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
});
ILogger<BotHandler> botLogger = loggerFactory.CreateLogger<BotHandler>();

using var cts = new CancellationTokenSource();

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

    var backup = new GitHubBackupService(
        owner:   Environment.GetEnvironmentVariable("GH_OWNER")  ?? throw new Exception("GH_OWNER не задан"),
        repo:    Environment.GetEnvironmentVariable("GH_REPO")   ?? throw new Exception("GH_REPO не задан"),
        ghToken: Environment.GetEnvironmentVariable("GH_TOKEN")  ?? throw new Exception("GH_TOKEN не задан")
    );

    await backup.RestoreAsync();

    // Миграция — создаём новые таблицы и колонки если их нет
    DbMigrator.Migrate();

    // Self-ping to prevent Render from sleeping
    _ = Task.Run(async () =>
    {
        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromSeconds(15);
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                await httpClient.GetAsync(appUrl, cts.Token);
                // Console.WriteLine("[Self-ping] OK");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Self-ping] Error: {ex.Message}");
            }
            await Task.Delay(TimeSpan.FromSeconds(49), cts.Token);
        }
    });

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

    var alarm = new AlarmService(bot);
    _ = alarm.RunAsync(cts.Token);

    var botHandler = new BotHandler(bot, backup, botLogger);

    // Self-ping to prevent Render from sleeping
    _ = Task.Run(async () =>
    {
        using var httpClient = new HttpClient();
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                await httpClient.GetAsync(appUrl, cts.Token);
                // Console.WriteLine("[Self-ping] OK");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Self-ping] Error: {ex.Message}");
            }
            await Task.Delay(TimeSpan.FromSeconds(49), cts.Token);
        }
    });

    Console.WriteLine("BOT STARTING...");

    string webhookUrl = $"{appUrl.TrimEnd('/')}/bot";
    await bot.SetWebhook(webhookUrl, cancellationToken: cts.Token);
    Console.WriteLine($"Webhook установлен: {webhookUrl}");

    var me = await bot.GetMe();
    Console.WriteLine($"Бот запущен: @{me.Username}");

    await RunWebServer(bot, botHandler, cts.Token);
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
async Task RunWebServer(ITelegramBotClient bot, BotHandler handler, CancellationToken ct)
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
                            await handler.HandleUpdateAsync(update, ct);
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
