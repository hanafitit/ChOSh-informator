using System.Collections.Generic;
using Telegram.Bot.Types.ReplyMarkups;
using ЧОШ_информатор.Models;

namespace ЧОШ_информатор.Helpers;

public static class KeyboardHelper
{
    public static ReplyKeyboardMarkup MainKeyboard() =>
        new(new[]
        {
            new[] { new KeyboardButton("📅 Расписание") },
            new[] { new KeyboardButton("📊 Опросы"), new KeyboardButton("👤 Профиль") }
        })
        { ResizeKeyboard = true };

    public static ReplyKeyboardMarkup AdminKeyboard() =>
        new(new[]
        {
            new[] { new KeyboardButton("📢 Объявление"), new KeyboardButton("📊 Упр. опросами") },
            new[] { new KeyboardButton("👨‍🏫 Учителя"), new KeyboardButton("📋 Проверить расписание") },
            new[] { new KeyboardButton("👤 Профиль") }
        })
        { ResizeKeyboard = true };

    public static ReplyKeyboardMarkup AdminPollKeyboard() =>
        new(new[]
        {
            new[] { new KeyboardButton("➕ Создать опрос"), new KeyboardButton("🛑 Остановить опрос") },
            new[] { new KeyboardButton("⬅️ Назад") }
        })
        { ResizeKeyboard = true };

    public static ReplyKeyboardMarkup TeacherKeyboard(Teacher t)
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
}
