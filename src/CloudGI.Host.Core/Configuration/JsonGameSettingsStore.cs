using System.Text.Json;

namespace CloudGI.Host.Core.Configuration;

public sealed class JsonGameSettingsStore(string filePath) : IGameSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    public string FilePath { get; } = Path.GetFullPath(filePath);

    public async Task<GameLaunchSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous);
            if (stream.Length > 65536)
                throw new InvalidDataException("配置文件过大，请在设置中重新保存。");

            var settings = await JsonSerializer.DeserializeAsync<GameLaunchSettings>(
                stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (settings is null || settings.SchemaVersion != 1 ||
                settings.GameExecutablePath is null || settings.LaunchArguments is null)
                throw new InvalidDataException("配置内容或版本不受支持，请在设置中重新保存。");
            return settings;
        }
        catch (FileNotFoundException) { return new GameLaunchSettings(); }
        catch (DirectoryNotFoundException) { return new GameLaunchSettings(); }
        catch (JsonException ex)
        {
            throw new InvalidDataException("配置文件无法读取，请在设置中重新保存。", ex);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(GameLaunchSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SchemaVersion != 1 || settings.GameExecutablePath is null || settings.LaunchArguments is null)
            throw new InvalidDataException("配置内容或版本不受支持。");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew,
                             FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _gate.Release();
        }
    }
}
