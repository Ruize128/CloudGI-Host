using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CloudGI.Host.Core.Streaming;

public sealed record SunshineSettings(string Name, int Port = 48989);
public enum SunshineState { Stopped, Starting, Ready, Failed }
public sealed record SunshineSnapshot(SunshineState State, string Name, int Port, int? ProcessId = null, string? Error = null, string? Encoder = null);
public sealed record PendingPairing(string Id, string Name, string Address)
{
    public string Label => string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} ({Address})";
}

public interface ISunshineService : IDisposable
{
    SunshineSnapshot Snapshot { get; }
    Task StartAsync(CancellationToken token = default);
    Task RenameAsync(string name, CancellationToken token = default);
    Task<IReadOnlyList<PendingPairing>> GetPendingPairingsAsync(CancellationToken token = default);
    Task PairAsync(string id, string pin, string name, CancellationToken token = default);
}

/// <summary>Configuration for CloudGI's private instance only. Never reads an installed Sunshine's data.</summary>
public sealed class SunshineConfiguration(string directory, bool childSession = false)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string FilePath(string name) => Path.Combine(DirectoryPath, name);
    public const string Version = "2026.914.233613";
    public static readonly int[] TcpOffsets = [-5, 0, 1, 2, 21];
    public static readonly int[] UdpOffsets = [9, 10, 11];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string ValidateName(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || Encoding.UTF8.GetByteCount(name) > 128 ||
            name.Any(c => char.IsControl(c) || c is '#' or '='))
            throw new ArgumentException("主机名称不能为空，最长为 128 个 UTF-8 字节，且不能包含换行、# 或 =。");
        return name;
    }

    public async Task<SunshineSettings> LoadAsync(CancellationToken token = default)
    {
        var path = FilePath("instance.json");
        if (!File.Exists(path)) return new(ValidateName("CloudGI - " + Environment.MachineName));
        try
        {
            var settings = JsonSerializer.Deserialize<SunshineSettings>(await File.ReadAllTextAsync(path, token), JsonOptions)
                ?? throw new InvalidDataException("串流配置为空。");
            ValidateName(settings.Name);
            if (settings.Port is < 1029 or > 65514) throw new InvalidDataException("串流端口配置无效。");
            return settings;
        }
        catch (JsonException ex) { throw new InvalidDataException("串流配置损坏，原文件已保留。", ex); }
    }

    public async Task SaveAsync(SunshineSettings settings, CancellationToken token = default)
    {
        ValidateName(settings.Name);
        if (settings.Port is < 1029 or > 65514) throw new ArgumentOutOfRangeException(nameof(settings));
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath("instance." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(settings, JsonOptions), token);
            File.Move(temporary, FilePath("instance.json"), true);
        }
        finally { File.Delete(temporary); }
    }

    public string Render(SunshineSettings settings, bool useHardwareEncoding = false)
    {
        var name = ValidateName(settings.Name);
        // Explicit absolute paths avoid Sunshine's installation-wide default config directory.
        return $"""
            sunshine_name = {name}
            port = {settings.Port}
            system_tray = disabled
            upnp = disabled
            address_family = ipv4
            origin_web_ui_allowed = pc
            file_apps = {FilePath("apps.json")}
            file_state = {FilePath("state.json")}
            credentials_file = {FilePath("admin.json")}
            pkey = {FilePath("credentials/cakey.pem")}
            cert = {FilePath("credentials/cacert.pem")}
            log_path = {FilePath("sunshine.log")}
            native_pen_touch = enabled
            gamepad_driver = vigembus
            dd_configuration_option = disabled
            """ + Environment.NewLine + (childSession ? "capture = wgc" + Environment.NewLine : "")
            + (useHardwareEncoding ? "" : "encoder = software" + Environment.NewLine);
    }

    public async Task WriteAsync(SunshineSettings settings, CancellationToken token = default, bool useHardwareEncoding = false)
    {
        Directory.CreateDirectory(DirectoryPath);
        Directory.CreateDirectory(FilePath("credentials"));
        await File.WriteAllTextAsync(FilePath("sunshine.conf"), Render(settings, useHardwareEncoding), new UTF8Encoding(false), token);
        var statePath = FilePath("state.json");
        var existing = File.Exists(statePath) ? await File.ReadAllBytesAsync(statePath, token) : [];
        if (existing.Length == 0)
        {
            // A killed write can leave an empty file. It contains no recoverable identity/devices.
            // Commit a complete initial state atomically, including on first startup.
            var state = new { root = new { uniqueid = Guid.NewGuid().ToString().ToUpperInvariant(), named_devices = Array.Empty<object>() } };
            var temporary = FilePath("state." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, JsonOptions), new UTF8Encoding(false), token);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, statePath, true);
            }
            finally { File.Delete(temporary); }
        }
        else
        {
            // Never erase nonempty pairing data. Report corrupt state before launching Sunshine,
            // instead of advertising mDNS and waiting for an identity that cannot become valid.
            try
            {
                using var state = JsonDocument.Parse(existing);
                if (!state.RootElement.TryGetProperty("root", out var root) || root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("uniqueid", out var identity) || identity.ValueKind != JsonValueKind.String ||
                    !Guid.TryParse(identity.GetString(), out _))
                    throw new JsonException("Missing or invalid Sunshine host identity.");
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                throw new InvalidDataException("Sunshine 状态文件损坏，原文件已保留：" + statePath, ex);
            }
        }
        // This list belongs to CloudGI. A no-command Desktop entry starts streaming without launching a game.
        await File.WriteAllTextAsync(FilePath("apps.json"),
            """{"env":{},"apps":[{"name":"Desktop","image-path":"desktop.png"}]}""", token);
    }

    public static int FindAvailablePort(int preferred)
    {
        var candidates = new[] { preferred }.Concat(Enumerable.Range(0, 100).Select(i => 48989 + i * 100));
        foreach (var candidate in candidates.Distinct())
            if (CanBind(candidate)) return candidate;
        throw new IOException("没有找到可用的 Sunshine 端口组。");
    }

    public static bool CanBind(int port)
    {
        if (port is < 1029 or > 65514) return false;
        var sockets = new List<Socket>();
        try
        {
            foreach (var offset in TcpOffsets)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sockets.Add(socket);
                socket.ExclusiveAddressUse = true;
                socket.Bind(new IPEndPoint(IPAddress.Any, port + offset));
            }
            foreach (var offset in UdpOffsets)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sockets.Add(socket);
                socket.ExclusiveAddressUse = true;
                socket.Bind(new IPEndPoint(IPAddress.Any, port + offset));
            }
            return true;
        }
        catch (SocketException) { return false; }
        finally { foreach (var socket in sockets) socket.Dispose(); }
    }
}
