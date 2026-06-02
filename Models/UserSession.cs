using System.Collections.Generic;

namespace ЧОШ_информатор.Models;

public class UserSession
{
    public string State { get; set; } = "";
    public string Name { get; set; } = "";
    public Dictionary<string, string> TempData { get; set; } = new();

    public void Reset()
    {
        State = "";
        TempData.Clear();
    }
}
