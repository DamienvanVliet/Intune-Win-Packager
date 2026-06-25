using System.IO;
using System.Text.Json;

namespace IntuneWinPackager.Infrastructure.Persistence;

internal sealed class JsonFileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<T> ReadAsync<T>(string filePath, T fallback, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(filePath))
            {
                return fallback;
            }

            await using var stream = File.OpenRead(filePath);
            var deserialized = await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken);
            return deserialized ?? fallback;
        }
        catch
        {
            return fallback;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task WriteAsync<T>(string filePath, T payload, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        string? tempPath = null;
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            tempPath = Path.Combine(
                directory ?? AppContext.BaseDirectory,
                $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, payload, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(filePath))
            {
                File.Replace(tempPath, filePath, null);
            }
            else
            {
                File.Move(tempPath, filePath);
            }

            tempPath = null;
        }
        finally
        {
            TryDeleteTempFile(tempPath);
            _semaphore.Release();
        }
    }

    private static void TryDeleteTempFile(string? tempPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // Best-effort cleanup only; the next write uses a unique temp name.
        }
    }
}
