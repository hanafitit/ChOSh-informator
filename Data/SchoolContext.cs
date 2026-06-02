using Microsoft.EntityFrameworkCore;
using ЧОШ_информатор.Models;

namespace ЧОШ_информатор.Data;

public class SchoolContext : DbContext
{
    public DbSet<Schedule> Schedules { get; set; } = null!;
    public DbSet<User> Users { get; set; } = null!;
    public DbSet<Teacher> Teachers { get; set; } = null!;
    public DbSet<TeacherSubject> TeacherSubjects { get; set; } = null!;
    public DbSet<TeacherAttendance> TeacherAttendance { get; set; } = null!;
    public DbSet<Poll> Polls { get; set; } = null!;
    public DbSet<PollOption> PollOptions { get; set; } = null!;
    public DbSet<PollVote> PollVotes { get; set; } = null!;

    protected override void OnConfiguring(DbContextOptionsBuilder o)
        => o.UseSqlite("Data Source=school.db");

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Schedule>().ToTable("Schedule");
        m.Entity<User>().ToTable("Users");
        m.Entity<Teacher>().ToTable("Teachers");
        m.Entity<TeacherSubject>().ToTable("TeacherSubjects");
        m.Entity<TeacherAttendance>().ToTable("TeacherAttendance");
        m.Entity<Poll>().ToTable("Polls");
        m.Entity<PollOption>().ToTable("PollOptions");
        m.Entity<PollVote>().ToTable("PollVotes");
    }
}
