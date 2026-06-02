namespace ЧОШ_информатор.Models;

public class TeacherAttendance
{
    public int Id { get; set; }
    public string TeacherName { get; set; } = "";
    public string Date { get; set; } = "";
    public bool IsPresent { get; set; } = true;
}
