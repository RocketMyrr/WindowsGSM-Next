using System.Net;
using System.Net.Http.Headers;
using WindowsGSM.Contracts;
using WindowsGSM.Core.Tests;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Tests;

[Collection("Agent")]
public class FileAndBackupTests
{
    private readonly AgentFixture _f;
    public FileAndBackupTests(AgentFixture f) => _f = f;

    private static string Files(string rel = "") => ServerPath.GetServersServerFiles("103", rel);

    [Fact]
    public async Task Browse_edit_rename_and_delete_inside_the_server_folder()
    {
        Directory.CreateDirectory(Files("cfg"));
        File.WriteAllText(Files("cfg/server.cfg"), "hostname old\n");
        var owner = _f.Owner;

        var root = await owner.GetJsonAsync<FolderDto>(_f.ServerUrl("103", "/files"));
        Assert.Contains(root.Entries, e => e.Name == "cfg" && e.IsDirectory);

        var file = await owner.GetJsonAsync<TextFileDto>(_f.ServerUrl("103", "/files/content?path=cfg/server.cfg"));
        Assert.Equal("hostname old\n", file.Content);
        Assert.False(file.ReadOnly);

        var save = await owner.PutAsync(_f.ServerUrl("103", "/files/content"), new FileWriteRequest("cfg/server.cfg", "hostname new\n", file.Modified));
        Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);
        Assert.Equal("hostname new\n", File.ReadAllText(Files("cfg/server.cfg")));

        // Opened before someone else changed it → refused rather than silently overwriting.
        var stale = await owner.PutAsync(_f.ServerUrl("103", "/files/content"), new FileWriteRequest("cfg/server.cfg", "mine", file.Modified.AddMinutes(-5)));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync(_f.ServerUrl("103", "/files/folder"), new CreateFolderRequest("cfg", "maps"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync(_f.ServerUrl("103", "/files/folder"), new CreateFolderRequest("cfg", "maps"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync(_f.ServerUrl("103", "/files/rename"), new RenameRequest("cfg/maps", "worlds"))).StatusCode);
        Assert.True(Directory.Exists(Files("cfg/worlds")));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync(_f.ServerUrl("103", "/files/delete"), new PathRequest("cfg/worlds"))).StatusCode);
        Assert.False(Directory.Exists(Files("cfg/worlds")));
    }

    [Theory]
    [InlineData("../configs/WindowsGSM.cfg")]
    [InlineData("..\\..\\..\\secret.txt")]
    [InlineData("C:/Windows/win.ini")]
    public async Task Paths_outside_the_server_folder_are_refused(string path)
    {
        var res = await _f.Owner.GetAsync(_f.ServerUrl("103", "/files/content?path=" + Uri.EscapeDataString(path)));
        Assert.True(res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, $"{path} → {(int)res.StatusCode}");
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Owner.PostAsync(_f.ServerUrl("103", "/files/delete"), new PathRequest(""))).StatusCode); // never the root itself
    }

    [Fact]
    public async Task A_link_pointing_outside_the_server_folder_is_not_followed()
    {
        string outside = Path.Combine(Path.GetTempPath(), "wgsm-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "private.txt"), "secret");
        string link = Files("escape");
        try
        {
            // A junction needs no admin rights (unlike a symlink).
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!;
            p.WaitForExit(10000);
            Assert.True(Directory.Exists(link), "junction was created");

            var res = await _f.Owner.GetAsync(_f.ServerUrl("103", "/files/content?path=escape/private.txt"));
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

            // Deleting the link removes the link only, never the folder it points at.
            Assert.Equal(HttpStatusCode.NoContent, (await _f.Owner.PostAsync(_f.ServerUrl("103", "/files/delete"), new PathRequest("escape"))).StatusCode);
            Assert.True(File.Exists(Path.Combine(outside, "private.txt")));
        }
        finally
        {
            try { if (Directory.Exists(link)) { Directory.Delete(link); } } catch { }
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public async Task Uploads_land_in_the_chosen_folder()
    {
        Directory.CreateDirectory(Files("uploads"));
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent("mod data"u8.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "files", "mod.zip");
        var res = await _f.Owner.Http.PostAsync(_f.ServerUrl("103", "/files/upload?path=uploads"), form);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("mod data", File.ReadAllText(Files("uploads/mod.zip")));
    }

    [Fact]
    public async Task Backup_list_download_and_the_admin_only_settings()
    {
        var owner = _f.Owner;
        File.WriteAllText(Files("world.dat"), "world");
        var backup = await owner.PostAsync(_f.ServerUrl("103", "/backups"), new BackupRequest());
        Assert.Equal(HttpStatusCode.Accepted, backup.StatusCode);
        string jobId = (await ApiClient.Read<JobAccepted>(backup)).JobId;
        await EngineFixture.WaitUntil(() => owner.GetJsonAsync<JobDto>($"/api/v2/machines/local/jobs/{jobId}").GetAwaiter().GetResult().Status == "Succeeded", "backup job");

        var list = await owner.GetJsonAsync<List<BackupDto>>(_f.ServerUrl("103", "/backups"));
        var latest = list.OrderByDescending(b => b.Created).First();
        var download = await owner.GetAsync(_f.ServerUrl("103", $"/backups/{latest.Name}/download"));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType!.MediaType);

        // An operator can back up and adjust retention, but not point backups at folders outside the server.
        var op = await _f.UserAsync("backupop", Role.Operator);
        var settings = await op.GetJsonAsync<BackupSettingsDto>(_f.ServerUrl("103", "/backups/settings"));
        var retention = await op.PutAsync(_f.ServerUrl("103", "/backups/settings"), settings with { KeepCount = 5 });
        Assert.Equal(HttpStatusCode.NoContent, retention.StatusCode);
        var outside = await op.PutAsync(_f.ServerUrl("103", "/backups/settings"), settings with { KeepCount = 5, ExternalLocations = new[] { "C:\\Users" } });
        Assert.Equal(HttpStatusCode.Forbidden, outside.StatusCode);
        var escape = await op.PutAsync(_f.ServerUrl("103", "/backups/settings"), settings with { Paths = new[] { "../../configs" } });
        Assert.Equal(HttpStatusCode.BadRequest, escape.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(_f.ServerUrl("103", $"/backups/{latest.Name}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync(_f.ServerUrl("103", "/backups/..%2F..%2Fconfigs"))).StatusCode);
    }
}
