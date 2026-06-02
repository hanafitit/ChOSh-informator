namespace ЧОШ_информатор.Models;

public class Teacher
{
    public int Id { get; set; }
    public long TelegramId { get; set; }
    public string Name { get; set; } = "";
    public bool IsHomeroom { get; set; }
    public string HomeroomClass { get; set; } = "";
}
