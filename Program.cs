using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot.Types.ReplyMarkups;

// ─────────────────────────────────────────────
// 1. Читаем токен бота из переменной окружения
// ─────────────────────────────────────────────
var token = Environment.GetEnvironmentVariable("BOT_TOKEN")
    ?? throw new Exception("BOT_TOKEN не задан!");

// ─────────────────────────────────────────────
// 2. Создаём клиент Telegram бота
// ─────────────────────────────────────────────
var bot = new TelegramBotClient(token);

using var cts = new CancellationTokenSource();

// ─────────────────────────────────────────────
// 3. Запускаем получение обновлений (сообщений)
// ─────────────────────────────────────────────
bot.StartReceiving(
    updateHandler: HandleUpdate,
    errorHandler: HandleError,
    receiverOptions: new ReceiverOptions { AllowedUpdates = [] },
    cancellationToken: cts.Token
);

var me = await bot.GetMe();
Console.WriteLine($"Бот запущен: @{me.Username}");

// ─────────────────────────────────────────────
// 4. Ждём бесконечно
// ─────────────────────────────────────────────
await Task.Delay(Timeout.Infinite, cts.Token);


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

                await bot.SendMessage(chatId, "Главное меню:", replyMarkup: keyboard);
                break;
            case "📅 Расписание":
                var scheduleKeyboard = new ReplyKeyboardMarkup(new[]
                {
        new[] { new KeyboardButton("Сегодня"), new KeyboardButton("Завтра") },
        new[] { new KeyboardButton("Неделя") },
        new[] { new KeyboardButton("⬅️ Назад") }
    })
                { ResizeKeyboard = true };

                await bot.SendMessage(chatId, "Выбери вариант:", replyMarkup: scheduleKeyboard);
                break;
            case "📊 Опросы":
                await bot.SendMessage(chatId, "Здесь будут опросы");
                break;
            case "📢 Объявления":
                await bot.SendMessage(chatId, "Здесь будут объявления");
                break;
            case "👤 Профиль":
                await bot.SendMessage(chatId, $"Твой ID:{update.Message.Chat.Id}");
                break;

        }
        

        
    }

    // Заглушка для CallbackQuery, если понадобятся кнопки
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
