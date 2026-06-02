using System.Collections.Generic;

namespace ЧОШ_информатор.Models;

public class Poll
{
    public int Id { get; set; }
    public string Question { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public string CreatedAt { get; set; } = "";
    public List<PollOption> Options { get; set; } = new();
}

public class PollOption
{
    public int Id { get; set; }
    public int PollId { get; set; }
    public string Text { get; set; } = "";
    public int VoteCount { get; set; } = 0;
}

public class PollVote
{
    public int Id { get; set; }
    public int PollId { get; set; }
    public long UserTelegramId { get; set; }
    public int OptionId { get; set; }
}
