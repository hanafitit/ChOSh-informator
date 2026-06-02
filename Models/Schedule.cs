namespace ЧОШ_информатор.Models;

public class Schedule
{
    public int Id { get; set; }
    public string ClassName { get; set; } = "";
    public string DayOfWeek { get; set; } = "";
    public int LessonNumber { get; set; }
    public string Subject { get; set; } = "";
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public string TeacherName { get; set; } = "";
}
