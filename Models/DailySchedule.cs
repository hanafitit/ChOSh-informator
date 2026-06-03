namespace ЧОШ_информатор.Models;

public class DailySchedule
{
    public int Id { get; set; }
    public string Date { get; set; } = "";
    public string ClassName { get; set; } = "";
    public int LessonNumber { get; set; }
    public string Subject { get; set; } = "";
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public string TeacherName { get; set; } = "";
    public bool IsModified { get; set; } = false;
}
