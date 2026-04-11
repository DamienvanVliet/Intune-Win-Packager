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
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var stream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(stream, payload, SerializerOptions, cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
