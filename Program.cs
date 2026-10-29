using System.Diagnostics;
using System.Text.Json;

namespace AdbScrcpy;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Length == 0)
        {
            Console.WriteLine("Usage: dotnet run -- --config config.json [--dry-run | --once]\n" +
                              "--dry-run prints a plan without starting any external tools.\n" +
                              "--once reconciles once; the default monitors until Ctrl+C.");
            return 0;
        }
        try
        {
            var index = Array.IndexOf(args, "--config");
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Provide --config PATH");
            var config = JsonSerializer.Deserialize<Config>(await File.ReadAllTextAsync(args[index + 1]),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new ArgumentException("Empty configuration");
            config.Validate();
            if (args.Contains("--dry-run"))
            {
                foreach (var device in config.Devices)
                {
                    Console.WriteLine($"Connect: {config.Adb} connect {device.Serial}");
                    Console.WriteLine("Mirror: " + config.Scrcpy + " " +
                        string.Join(" ", Layout.Arguments(device, config.Devices.IndexOf(device), config)));
                }
                return 0;
            }
            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += handler;
            try
            {
                using var orchestrator = new Orchestrator(config);
                do
                {
                    await orchestrator.Reconcile(stop.Token);
                    if (args.Contains("--once")) break;
                    await Task.Delay(TimeSpan.FromSeconds(config.PollSeconds), stop.Token);
                } while (!stop.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally { Console.CancelKeyPress -= handler; }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}

public sealed record Device(string Name, string Serial);

public sealed class Config
{
    public string Adb { get; set; } = "adb";
    public string Scrcpy { get; set; } = "scrcpy";
    public int Columns { get; set; } = 2;
    public int Width { get; set; } = 640;
    public int Height { get; set; } = 480;
    public int OriginX { get; set; }
    public int OriginY { get; set; }
    public int PollSeconds { get; set; } = 5;
    public int CommandTimeoutSeconds { get; set; } = 15;
    public List<Device> Devices { get; set; } = [];

    public void Validate()
    {
        if (Columns < 1 || Width < 1 || Height < 1 || PollSeconds < 1 || CommandTimeoutSeconds < 1)
            throw new ArgumentException("Grid, timeouts and poll interval must be positive");
        if (Devices.Count == 0 || Devices.Any(d => string.IsNullOrWhiteSpace(d.Name) ||
            string.IsNullOrWhiteSpace(d.Serial) || d.Serial.StartsWith('-') ||
            d.Serial.Any(char.IsWhiteSpace)))
            throw new ArgumentException("Provide at least one named device with a valid ADB serial");
        if (Devices.Select(d => d.Serial).Distinct(StringComparer.Ordinal).Count() != Devices.Count)
            throw new ArgumentException("Device serials must be unique");
    }
}

public static class Layout
{
    public static string[] Arguments(Device device, int index, Config config) =>
    [
        "--serial", device.Serial, "--window-title", device.Name,
        "--window-x", (config.OriginX + index % config.Columns * config.Width).ToString(),
        "--window-y", (config.OriginY + index / config.Columns * config.Height).ToString(),
        "--window-width", config.Width.ToString(), "--window-height", config.Height.ToString()
    ];
}

public sealed class Orchestrator(Config config) : IDisposable
{
    private readonly Dictionary<string, Process> mirrors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> retryAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> failures = new(StringComparer.Ordinal);

    public static Dictionary<string, string> ParseDevices(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 2 && columns[1] is "device" or "offline" or "unauthorized")
                result[columns[0]] = columns[1];
        }
        return result;
    }

    public async Task Reconcile(CancellationToken cancellation)
    {
        var snapshot = ParseDevices(await Run(config.Adb, ["devices", "-l"], cancellation));
        for (var index = 0; index < config.Devices.Count; index++)
        {
            var device = config.Devices[index];
            if (retryAt.TryGetValue(device.Serial, out var next) && next > DateTimeOffset.UtcNow) continue;
            try
            {
                snapshot.TryGetValue(device.Serial, out var state);
                if (state == "unauthorized")
                {
                    Console.WriteLine($"{device.Name}: authorize this computer on the device");
                    Backoff(device.Serial);
                    continue;
                }
                if (state != "device")
                {
                    StopOwnedMirror(device.Serial);
                    // TCP/IP pairing/authorization is performed by the device owner first.
                    // USB serials cannot be reconnected with `adb connect`.
                    if (!device.Serial.Contains(':'))
                    {
                        Backoff(device.Serial);
                        continue;
                    }
                    await Run(config.Adb, ["connect", device.Serial], cancellation);
                    var fresh = ParseDevices(await Run(config.Adb, ["devices", "-l"], cancellation));
                    if (!fresh.TryGetValue(device.Serial, out state) || state != "device")
                    {
                        Console.WriteLine($"{device.Name}: not online yet; retry scheduled");
                        Backoff(device.Serial);
                        continue;
                    }
                }
                if (mirrors.TryGetValue(device.Serial, out var existing))
                {
                    if (!existing.HasExited) continue;
                    existing.Dispose();
                    mirrors.Remove(device.Serial);
                    // A rejected scrcpy option or crashed process must not create a restart storm.
                    Backoff(device.Serial);
                    continue;
                }
                var info = CreateStart(config.Scrcpy, Layout.Arguments(device, index, config));
                var process = Process.Start(info) ?? throw new IOException("Unable to start scrcpy");
                mirrors[device.Serial] = process;
                Console.WriteLine($"{device.Name}: mirror started ({process.Id})");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                Console.Error.WriteLine($"{device.Name}: {error.Message}");
                Backoff(device.Serial);
            }
        }
    }

    private void Backoff(string serial)
    {
        var attempts = Math.Min(failures.GetValueOrDefault(serial) + 1, 6);
        failures[serial] = attempts;
        retryAt[serial] = DateTimeOffset.UtcNow.AddSeconds(Math.Min(60, Math.Pow(2, attempts)));
    }

    private static ProcessStartInfo CreateStart(string executable, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var value in args) info.ArgumentList.Add(value);
        return info;
    }

    private async Task<string> Run(string executable, IEnumerable<string> args, CancellationToken cancellation)
    {
        var info = CreateStart(executable, args);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using var process = Process.Start(info) ?? throw new IOException("Unable to start ADB");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(config.CommandTimeoutSeconds));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (cancellation.IsCancellationRequested) throw;
            throw new TimeoutException("ADB command timed out");
        }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new IOException("ADB failed: " + error.Trim());
        return output;
    }

    private void StopOwnedMirror(string serial)
    {
        if (!mirrors.Remove(serial, out var process)) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        finally { process.Dispose(); }
    }

    public void Dispose()
    {
        foreach (var serial in mirrors.Keys.ToArray()) StopOwnedMirror(serial);
    }
}
