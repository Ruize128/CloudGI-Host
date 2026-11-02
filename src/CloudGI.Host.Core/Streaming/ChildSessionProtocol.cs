using System.Buffers.Binary;
using System.Text.Json;
using CloudGI.Host.Core.Games;

namespace CloudGI.Host.Core.Streaming;

public sealed record ChildDisplaySettings(int Width = 1920, int Height = 1080, int Scale = 100)
{
    public ChildDisplaySettings Validate()
    {
        if (Width is < 200 or > 8192 || Height is < 200 or > 8192 || Scale is < 100 or > 500)
            throw new ArgumentException("子会话宽高须为 200～8192，缩放须为 100～500%。");
        return this;
    }
}

// Local-only protocol. Paths/commands for game and Sunshine remain owned by the agent.
public sealed record ChildSessionRequest(string Command, string? Value = null, string? Pin = null, string? Name = null);
public sealed record ChildSessionReply(bool Success, SunshineSnapshot Sunshine, GameRuntimeSnapshot? Game = null,
    GameLaunchResult? Launch = null, PendingPairing[]? Pending = null, string? Error = null);

public static class ChildSessionProtocol
{
    public const int MaximumMessageBytes = 65536;
    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length > MaximumMessageBytes) throw new InvalidDataException("子会话消息过大。");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaximumMessageBytes) throw new InvalidDataException("子会话消息长度无效。");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("子会话消息为空。");
    }
}
