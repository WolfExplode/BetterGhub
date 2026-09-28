using System.Diagnostics;
using System.Text.Json;

namespace BetterGhub.Device;

/// <summary>Runs bridge/hidpp_bridge.py --service and exchanges JSON lines with it.</summary>
internal sealed class DeviceBridge : IDisposable
{
    private Process? process;
    public event Action<JsonElement>? Message;
    public event Action<string>? Error;
    public event Action? Exited;
    public bool Running => process is { HasExited: false };

    /// <summary>Walks up from the executable to the BetterGhub folder that holds bridge/ and .venv/.</summary>
    public static string? FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "bridge", "hidpp_bridge.py")))
                return directory.FullName;
        return null;
    }

    public void Start()
    {
        if (Running) return;
        string root = FindRoot() ?? throw new FileNotFoundException("bridge\\hidpp_bridge.py was not found next to the app.");
        string python = Path.Combine(root, ".venv", "Scripts", "python.exe");
        if (!File.Exists(python))
            throw new FileNotFoundException("The Python environment is missing. Run setup.ps1 in the BetterGhub folder.");
        Process started = new()
        {
            StartInfo = new ProcessStartInfo(python)
            {
                ArgumentList = { "-u", Path.Combine(root, "bridge", "hidpp_bridge.py"), "--service" },
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        started.Start();
        process = started;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                string? line = await started.StandardOutput.ReadLineAsync();
                if (line is null) break;
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    Message?.Invoke(document.RootElement.Clone());
                }
                catch (JsonException) { Error?.Invoke(line); }
            }
            Exited?.Invoke();
        });
        _ = Task.Run(async () =>
        {
            string stderr = await started.StandardError.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(stderr)) Error?.Invoke(stderr.Trim());
        });
    }

    public void Send(string command, int value)
    {
        if (!Running) throw new InvalidOperationException("Connect the mouse first.");
        process!.StandardInput.WriteLine(JsonSerializer.Serialize(new { command, value }));
        process.StandardInput.Flush();
    }

    public void Stop()
    {
        if (process is null) return;
        Process current = process;
        process = null;
        try
        {
            if (!current.HasExited)
            {
                current.StandardInput.WriteLine("{\"command\":\"quit\"}");
                current.StandardInput.Flush();
                if (!current.WaitForExit(1500)) current.Kill();
            }
        }
        catch (Exception) { /* The process may have exited during shutdown. */ }
        current.Dispose();
    }

    public void Dispose() => Stop();
}
