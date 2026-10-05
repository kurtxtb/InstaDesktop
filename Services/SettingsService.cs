using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using InstaDesktop.Models;

namespace InstaDesktop.Services;

public sealed class SettingsService
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public AppSettings Current { get; private set; } = new();
    public event System.Action? Changed;

    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(AppPaths.Settings)) return;
            await using var stream = File.OpenRead(AppPaths.Settings);
            Current = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions) ?? new();
            Current.Normalize();
        }
        catch (System.Exception e) when (e is IOException or System.UnauthorizedAccessException or JsonException)
        {
            LoggingService.Write(LogEvent.UnexpectedException, e);
            Current = new();
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        var snapshot = settings.Copy();
        snapshot.Normalize();
        await _writeGate.WaitAsync();
        try { await WriteAsync(snapshot); }
        finally { _writeGate.Release(); }
        Changed?.Invoke();
    }

    // Changes only the given fields of the latest settings, so independent
    // background writers (window placement, zoom) cannot undo each other.
    public async Task UpdateAsync(System.Action<AppSettings> change)
    {
        await _writeGate.WaitAsync();
        try
        {
            var snapshot = Current.Copy();
            change(snapshot);
            snapshot.Normalize();
            await WriteAsync(snapshot);
        }
        finally { _writeGate.Release(); }
        Changed?.Invoke();
    }

    private async Task WriteAsync(AppSettings snapshot)
    {
        Directory.CreateDirectory(AppPaths.Root);
        string temporary = AppPaths.Settings + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(temporary, AppPaths.Settings, overwrite: true);
        Current = snapshot;
    }
}
