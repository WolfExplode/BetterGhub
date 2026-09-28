using System.Diagnostics;
using System.Text.Json;

namespace BetterGhub.App;

internal sealed class DeviceBridge : IDisposable
{
    private Process? process;
    public event Action<JsonElement>? Message;
    public event Action<string>? Error;
    public bool Running => process is { HasExited: false };

    public void Start()
    {
        if (Running) return;
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string python = Path.Combine(root, ".venv", "Scripts", "python.exe");
        string script = Path.Combine(root, "host_mode_probe.py");
        if (!File.Exists(python) || !File.Exists(script))
            throw new FileNotFoundException("Device bridge missing. Run setup.ps1 from the BetterGhub folder.");
        process = new Process
        {
            StartInfo = new ProcessStartInfo(python)
            {
                ArgumentList = { "-u", script, "--service" },
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        process.Start();
        _ = Task.Run(async () =>
        {
            while (process is { HasExited: false } active)
            {
                string? line = await active.StandardOutput.ReadLineAsync();
                if (line is null) break;
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    Message?.Invoke(document.RootElement.Clone());
                }
                catch (JsonException) { Error?.Invoke(line); }
            }
            Error?.Invoke("Device bridge stopped");
        });
        _ = Task.Run(async () =>
        {
            if (process is null) return;
            string stderr = await process.StandardError.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(stderr)) Error?.Invoke(stderr.Trim());
        });
    }

    public void Send(string command, int value)
    {
        if (!Running) throw new InvalidOperationException("Connect the mouse first.");
        process!.StandardInput.WriteLine(JsonSerializer.Serialize(new { command, value }));
        process.StandardInput.Flush();
    }

    public void Dispose()
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine("{\"command\":\"quit\"}");
                process.StandardInput.Flush();
                if (!process.WaitForExit(1200)) process.Kill();
            }
        }
        catch (Exception) { /* The process may have exited during shutdown. */ }
        process.Dispose();
        process = null;
    }
}
