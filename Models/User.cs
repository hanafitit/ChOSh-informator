namespace ЧОШ_информатор.Models;

public class User
{
    public int Id { get; set; }
    public long TelegramId { get; set; }
    public string FirstName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string Role { get; set; } = "student";
}
