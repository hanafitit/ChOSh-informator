using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

Console.OutputEncoding = Encoding.UTF8;

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

    var bot = new TelegramBotClient(token);

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
// ══════════════════════════════════════════════
async Task HandleUpdate(ITelegramBotClient botClient, Update update, CancellationToken ct)
{
    if (update.Message?.Text is { } text)
    {
        var chatId = update.Message.Chat.Id;

        switch (text)
        {
            case "/start":
                var keyboard = new ReplyKeyboardMarkup(new[]
                {
                    new[] { new KeyboardButton("📅 Расписание"), new KeyboardButton("📢 Объявления") },
                    new[] { new KeyboardButton("📊 Опросы"),     new KeyboardButton("👤 Профиль") }
                })
                { ResizeKeyboard = true };
                await botClient.SendMessage(chatId, "Главное меню:", replyMarkup: keyboard, cancellationToken: ct);
                break;

            case "📅 Расписание":
                var scheduleKeyboard = new ReplyKeyboardMarkup(new[]
                {
                    new[] { new KeyboardButton("Сегодня"), new KeyboardButton("Завтра") },
                    new[] { new KeyboardButton("Неделя") },
                    new[] { new KeyboardButton("⬅️ Назад") }
                })
                { ResizeKeyboard = true };
                await botClient.SendMessage(chatId, "Выбери вариант:", replyMarkup: scheduleKeyboard, cancellationToken: ct);
                break;

            case "📊 Опросы":
                await botClient.SendMessage(chatId, "Здесь будут опросы", cancellationToken: ct);
                break;

            case "📢 Объявления":
                await botClient.SendMessage(chatId, "Здесь будут объявления", cancellationToken: ct);
                break;

            case "👤 Профиль":
                await botClient.SendMessage(chatId, $"Твой ID: {update.Message.Chat.Id}", cancellationToken: ct);
                break;

            case "⬅️ Назад":
                var mainKeyboard = new ReplyKeyboardMarkup(new[]
                {
                    new[] { new KeyboardButton("📅 Расписание"), new KeyboardButton("📢 Объявления") },
                    new[] { new KeyboardButton("📊 Опросы"),     new KeyboardButton("👤 Профиль") }
                })
                { ResizeKeyboard = true };
                await botClient.SendMessage(chatId, "Главное меню:", replyMarkup: mainKeyboard, cancellationToken: ct);
                break;
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
    return Task.CompletedTask;
}

// ══════════════════════════════════════════════
// ВЕБ-СЕРВЕР ДЛЯ RENDER
// ══════════════════════════════════════════════
static async Task RunWebServer(CancellationToken ct)
{
    var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
    var listener = new HttpListener();
    listener.Prefixes.Add($"http://*:{port}/");
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
