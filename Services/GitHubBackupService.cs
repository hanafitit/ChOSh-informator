using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Octokit;

namespace ЧОШ_информатор.Services;

public class GitHubBackupService
{
    private readonly string _owner, _repo, _token;
    private const string DbPath = "school.db";
    private const string FilePath = "backups/school.db";

    public GitHubBackupService(string owner, string repo, string ghToken)
    {
        _owner = owner;
        _repo = repo;
        _token = ghToken;
    }

    public async Task BackupAsync()
    {
        try
        {
            var client = CreateClient();
            byte[] bytes = await File.ReadAllBytesAsync(DbPath);
            string content = Convert.ToBase64String(bytes);

            RepositoryContentInfo? existing = null;
            try
            {
                existing = (await client.Repository.Content.GetAllContents(_owner, _repo, FilePath))[0];
            }
            catch (NotFoundException) { }

            if (existing != null)
                await client.Repository.Content.UpdateFile(_owner, _repo, FilePath,
                    new UpdateFileRequest($"db backup {DateTime.UtcNow:yyyy-MM-dd HH:mm}", content, existing.Sha));
            else
                await client.Repository.Content.CreateFile(_owner, _repo, FilePath,
                    new CreateFileRequest($"db backup {DateTime.UtcNow:yyyy-MM-dd HH:mm}", content));

            Console.WriteLine($"[Backup] БД сохранена: {DateTime.UtcNow:HH:mm:ss}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Backup] Ошибка: {ex.Message}");
        }
    }

    public async Task RestoreAsync()
    {
        try
        {
            var client = CreateClient();
            var contents = await client.Repository.Content.GetAllContents(_owner, _repo, FilePath);
            string base64 = contents[0].EncodedContent
                .Replace("\n", "").Replace("\r", "").Replace(" ", "");

            byte[] bytes = Convert.FromBase64String(base64);

            // Check if it's already a valid SQLite file or if it's double-encoded
            // (based on the original code logic)
            string decoded = Encoding.UTF8.GetString(bytes);
            if (decoded.StartsWith("U1FM") || !decoded.StartsWith("SQLite"))
                bytes = Convert.FromBase64String(decoded.Replace("\n", "").Replace("\r", "").Replace(" ", ""));

            await File.WriteAllBytesAsync(DbPath, bytes);
            Console.WriteLine("[Restore] БД восстановлена.");
        }
        catch (NotFoundException)
        {
            Console.WriteLine("[Restore] Бэкапа нет — используется локальная БД.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Restore] Ошибка: {ex.Message}");
        }
    }

    private GitHubClient CreateClient()
    {
        var c = new GitHubClient(new ProductHeaderValue("SchoolBot"));
        c.Credentials = new Credentials(_token);
        return c;
    }
}
