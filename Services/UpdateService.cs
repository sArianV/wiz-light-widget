using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace WizLightWidget.Services;

public class UpdateInfo
{
    public required string Version { get; init; }
    public required string TagName { get; init; }
    public required string DownloadUrl { get; init; }
}

public class UpdateService
{
    private const string ApiUrl = "https://api.github.com/repos/sArianV/wiz-light-widget/releases/latest";

    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WizLightWidget-Updater");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var json = await http.GetStringAsync(ApiUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString() ?? "";
            var versionStr = tagName.TrimStart('v');
            if (!Version.TryParse(versionStr, out var latestVersion))
                return null;

            var currentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            if (latestVersion <= currentVersion)
                return null;

            string? downloadUrl = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

            if (downloadUrl == null) return null;

            return new UpdateInfo { Version = versionStr, TagName = tagName, DownloadUrl = downloadUrl };
        }
        catch
        {
            // Sin internet, repo inaccesible, límite de rate de GitHub, etc.
            // No es crítico: la app simplemente sigue con la versión actual.
            return null;
        }
    }

    public async Task<string> DownloadUpdateAsync(string downloadUrl, IProgress<double>? progress = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WizLightWidget-Updater");

        var tempPath = Path.Combine(Path.GetTempPath(), $"WizLightWidget_update_{Guid.NewGuid():N}.exe");

        using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1L;

        await using var httpStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = File.Create(tempPath);

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await httpStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read));
            readTotal += read;
            if (total > 0) progress?.Report((double)readTotal / total);
        }

        return tempPath;
    }

    /// <summary>
    /// Lanza un script que espera a que este proceso termine, reemplaza el .exe actual
    /// por el nuevo, lo vuelve a abrir, y luego cierra esta instancia.
    /// </summary>
    public static void LaunchUpdateAndExit(string newExePath, string targetExePath)
    {
        int pid = Environment.ProcessId;
        string script =
            "@echo off\r\n" +
            ":wait\r\n" +
            $"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL\r\n" +
            "if not errorlevel 1 (\r\n" +
            "    timeout /t 1 /nobreak >NUL\r\n" +
            "    goto wait\r\n" +
            ")\r\n" +
            $"move /Y \"{newExePath}\" \"{targetExePath}\"\r\n" +
            $"start \"\" \"{targetExePath}\"\r\n" +
            "del \"%~f0\"\r\n";

        string scriptPath = Path.Combine(Path.GetTempPath(), $"wiz_update_{Guid.NewGuid():N}.bat");
        File.WriteAllText(scriptPath, script);

        var psi = new ProcessStartInfo
        {
            FileName = scriptPath,
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        Process.Start(psi);

        Environment.Exit(0);
    }
}
