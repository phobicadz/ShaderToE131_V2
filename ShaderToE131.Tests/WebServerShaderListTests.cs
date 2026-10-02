using System.Net.Sockets;
using System.Text.Json;
using Xunit;
using ShaderToE131;

/// <summary>
/// Regression tests for the flaky shader dropdown: the shader list must always be
/// served as a complete snapshot, even while the render loop re-scans the directory
/// and the browser polls several endpoints at once.
/// </summary>
public class WebServerShaderListTests
{
    private static string ShaderDir()
    {
        // Walk up to the repo root and find the shaders folder.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "shaders");
            if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "*.glsl").Length > 0) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("shader dir not found from " + AppContext.BaseDirectory);
    }

    private static async Task<string> GetAsync(string url)
    {
        // Raw socket read: proves the server actually delivered the whole body.
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", Port);
        var stream = client.GetStream();
        var req = System.Text.Encoding.ASCII.GetBytes($"GET {new Uri(url).PathAndQuery} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(req);
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        while (true)
        {
            int n = await stream.ReadAsync(buf);
            if (n <= 0) break;
            ms.Write(buf, 0, n);
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static int Port = new Random().Next(21000, 29000);

    [Fact]
    public async Task ShaderListIsCompleteUnderConcurrentLoad()
    {
        var dir = ShaderDir();
        int expected = Directory.GetFiles(dir, "*.glsl").Length;
        Assert.True(expected > 0);

        using var ws = new WebServer(Port, dir, "localhost");
        ws.Start();
        await Task.Delay(200);

        // Mimic the render loop, which refreshes the shader list every frame.
        using var cts = new CancellationTokenSource();
        var pump = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                ws.RefreshShaderList();
                try { Task.Delay(16, cts.Token).GetAwaiter().GetResult(); } catch { }
            }
        });

        try
        {
            var problems = new List<string>();
            for (int round = 0; round < 10; round++)
            {
                var tasks = Enumerable.Range(0, 8)
                    .Select(async _ => new[]
                    {
                        await GetAsync($"http://127.0.0.1:{Port}/api/shaders"),
                        await GetAsync($"http://127.0.0.1:{Port}/api/status"),
                        await GetAsync($"http://127.0.0.1:{Port}/"),
                    })
                    .ToArray();
                var responses = await Task.WhenAll(tasks);

                foreach (var r in responses.SelectMany(x => x))
                {
                    if (!r.Contains("\r\n\r\n")) { problems.Add("truncated headers: " + r[..Math.Min(60, r.Length)]); continue; }
                    string body = r.Substring(r.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4);
                    if (r.Contains("text/html"))
                    {
                        if (!body.TrimEnd().EndsWith("</html>")) problems.Add("truncated index page (" + body.Length + " bytes)");
                        continue;
                    }
                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(body); }
                    catch { problems.Add("unparseable JSON (" + body.Length + " bytes): " + body[..Math.Min(60, body.Length)]); continue; }
                    if (doc.RootElement.TryGetProperty("shaders", out var arr))
                    {
                        int n = arr.GetArrayLength();
                        if (n != expected) problems.Add($"shader list has {n} of {expected} entries");
                    }
                    doc.Dispose();
                }
            }
            Assert.Empty(problems);
        }
        finally
        {
            cts.Cancel();
            await pump;
        }
    }
}
