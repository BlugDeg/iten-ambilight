using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace ItenAmbilight
{
    /// <summary>
    /// Sucht auf GitHub nach einer neueren Version und ersetzt auf Wunsch die eigene .exe.
    /// Es wird ausschliesslich das unten genannte Repository über HTTPS verwendet.
    /// </summary>
    public static class Updater
    {
        /// <summary>Offizielles Repository (Besitzer/Name). Releases dort gelten als Updates.</summary>
        public const string Repo = "BlugDeg/iten-ambilight";
        /// <summary>Name der Datei im Release, die das Programm ist.</summary>
        public const string AssetName = "ItenAmbilight.exe";

        public sealed class UpdateInfo
        {
            public Version Version = new Version(0, 0, 0);
            public string Tag = "";
            public string DownloadUrl = "";
            public string PageUrl = "";
            public string Notes = "";
        }

        public static Version CurrentVersion
        {
            get
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        private static HttpClient NewClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ItenAmbilight/" + CurrentVersion);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        /// <summary>Liefert das neueste Release, wenn es neuer ist als dieses Programm; sonst null. Wirft bei Netzwerkfehlern.</summary>
        public static async Task<UpdateInfo?> CheckAsync()
        {
            using HttpClient client = NewClient();
            using HttpResponseMessage response = await client.GetAsync("https://api.github.com/repos/" + Repo + "/releases/latest");
            if ((int)response.StatusCode == 404) return null;          // noch kein Release (oder Repository nicht öffentlich)
            response.EnsureSuccessStatusCode();
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() ?? "" : "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? version) || version == null) return null;
            version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
            if (version <= CurrentVersion) return null;

            var info = new UpdateInfo
            {
                Version = version,
                Tag = tag,
                PageUrl = root.TryGetProperty("html_url", out JsonElement h) ? h.GetString() ?? "" : "",
                Notes = root.TryGetProperty("body", out JsonElement b) ? b.GetString() ?? "" : "",
            };
            if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement a in assets.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
                    if (!string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                    info.DownloadUrl = a.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() ?? "" : "";
                }
            }
            // Nur Dateien aus dem offiziellen Repository annehmen
            string prefix = "https://github.com/" + Repo + "/releases/download/";
            if (!info.DownloadUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            return info;
        }

        /// <summary>Lädt die neue .exe in den Temp-Ordner und gibt ihren Pfad zurück.</summary>
        public static async Task<string> DownloadAsync(UpdateInfo info)
        {
            string target = Path.Combine(Path.GetTempPath(), "ItenAmbilight-update-" + info.Version + ".exe");
            using HttpClient client = NewClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            using HttpResponseMessage response = await client.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            using (FileStream file = File.Create(target))
                await response.Content.CopyToAsync(file);
            if (new FileInfo(target).Length < 1_000_000) throw new IOException("Die heruntergeladene Datei ist unvollständig.");
            return target;
        }

        /// <summary>
        /// Startet ein kleines Skript, das wartet, bis dieses Programm beendet ist, die .exe ersetzt und neu startet.
        /// Danach muss das Programm sofort beendet werden.
        /// </summary>
        public static void InstallAfterExit(string newExe)
        {
            string current = Environment.ProcessPath ?? throw new InvalidOperationException("Programmpfad unbekannt.");
            int pid = Environment.ProcessId;
            string script = Path.Combine(Path.GetTempPath(), "ItenAmbilight-update.cmd");
            File.WriteAllText(script,
                "@echo off\r\n" +
                ":wait\r\n" +
                "tasklist /FI \"PID eq " + pid + "\" 2>nul | find \"" + pid + "\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
                "copy /Y \"" + newExe + "\" \"" + current + "\" >nul\r\n" +
                "start \"\" \"" + current + "\"\r\n" +
                "del \"" + newExe + "\" >nul 2>&1\r\n" +
                "del \"%~f0\" >nul 2>&1\r\n");
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + script + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
    }
}
