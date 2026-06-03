using Microsoft.Data.Sqlite;

namespace ЧОШ_информатор.Data;

public static class DbMigrator
{
    public static void Migrate()
    {
        using var conn = new SqliteConnection("Data Source=school.db");
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

        // Username в Teachers
        try
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE Teachers ADD COLUMN Username TEXT NOT NULL DEFAULT ''";
            cmd.ExecuteNonQuery();
            Console.WriteLine("[Migration] Добавлена колонка Username в Teachers");
        }
        catch { /* уже есть */ }

        // Таблица Teachers
        ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS Teachers (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            TelegramId INTEGER NOT NULL,
            Username TEXT NOT NULL DEFAULT '',
            Name TEXT NOT NULL DEFAULT '',
            IsHomeroom INTEGER NOT NULL DEFAULT 0,
            HomeroomClass TEXT NOT NULL DEFAULT ''
        )");

        // Таблица TeacherSubjects
        ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS TeacherSubjects (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            TeacherName TEXT NOT NULL DEFAULT '',
            Subject TEXT NOT NULL DEFAULT '',
            ClassName TEXT NOT NULL DEFAULT ''
        )");

        // Таблица TeacherAttendance
        ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS TeacherAttendance (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            TeacherName TEXT NOT NULL DEFAULT '',
            Date TEXT NOT NULL DEFAULT '',
            IsPresent INTEGER NOT NULL DEFAULT 1
        )");

        // Таблица DailySchedules
        ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS DailySchedules (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Date TEXT NOT NULL DEFAULT '',
            ClassName TEXT NOT NULL DEFAULT '',
            LessonNumber INTEGER NOT NULL DEFAULT 0,
            Subject TEXT NOT NULL DEFAULT '',
            StartTime TEXT NOT NULL DEFAULT '',
            EndTime TEXT NOT NULL DEFAULT '',
            TeacherName TEXT NOT NULL DEFAULT '',
            IsModified INTEGER NOT NULL DEFAULT 0
        )");

        // Таблица Polls
        ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS Polls (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Question TEXT NOT NULL,
            IsActive INTEGER NOT NULL DEFAULT 1,
            CreatedAt TEXT NOT NULL
        )");

        // Таблица PollOptions
        ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS PollOptions (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            PollId INTEGER NOT NULL,
            Text TEXT NOT NULL,
            VoteCount INTEGER NOT NULL DEFAULT 0
        )");

        // Таблица PollVotes
        ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS PollVotes (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            PollId INTEGER NOT NULL,
            UserTelegramId INTEGER NOT NULL,
            OptionId INTEGER NOT NULL
        )");

        Console.WriteLine("[Migration] БД актуальна.");
    }

    private static void ExecuteNonQuery(SqliteConnection conn, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
