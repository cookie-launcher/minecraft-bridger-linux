namespace Bridger.Core;

// Orijinal: KlasorJavaExe, SistemJavaSurumu, GerekliJavaSurumu,
// JavaCalismaZamaniSaglaAsync, JarIndirAsync.
// Linux: PATH + JAVA_HOME + yaygın dizinler taranır, indirme /tmp yerine VeriKok'a.
public static class JavaRuntime
{
    public static string? SistemJavasiniBul()
    {
        string? home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(home))
        {
            string exe = Path.Combine(home, "bin", "java");
            if (File.Exists(exe)) return exe;
        }
        foreach (string dir in new[] { "/usr/lib/jvm", "/usr/lib64/jvm", "/opt" })
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (string jvm in Directory.GetDirectories(dir))
                {
                    string exe = Path.Combine(jvm, "bin", "java");
                    if (File.Exists(exe)) return exe;
                }
            }
            catch { }
        }
        foreach (string p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
        {
            try
            {
                string exe = Path.Combine(p, "java");
                if (File.Exists(exe)) return exe;
            }
            catch { }
        }
        return null;
    }

    public static int? SurumuOku(string javaExe, int zamanAsimiMs = 8000)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(javaExe, "-version")
            {
                RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false
            };
            using var pr = System.Diagnostics.Process.Start(psi)!;
            string err = pr.StandardError.ReadToEnd();
            pr.WaitForExit(zamanAsimiMs);
            var m = System.Text.RegularExpressions.Regex.Match(err + pr.StandardOutput.ReadToEnd(), "\"(\\d+)[\\.\\-_]");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int v))
            {
                if (v == 1) // "1.8.0_xx" eski şema
                {
                    var m2 = System.Text.RegularExpressions.Regex.Match(err, "\"1\\.(\\d+)");
                    if (m2.Success) return int.Parse(m2.Groups[1].Value);
                }
                return v;
            }
        }
        catch { }
        return null;
    }

    public static async Task<string> JarIndirAsync(HttpClient http, string url, string hedef, IProgress<double>? ilerleme, CancellationToken t)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(hedef)!);
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, t);
        res.EnsureSuccessStatusCode();
        long? toplam = res.Content.Headers.ContentLength;
        await using var src = await res.Content.ReadAsStreamAsync(t);
        await using var dst = File.Create(hedef);
        var buf = new byte[81920]; long okunan = 0; int n;
        while ((n = await src.ReadAsync(buf, 0, buf.Length, t)) > 0)
        {
            await dst.WriteAsync(buf, 0, n, t);
            okunan += n;
            if (toplam > 0) ilerleme?.Report((double)okunan / toplam.Value * 100.0);
        }
        return hedef;
    }
}
