using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InstaDesktop.Services;

internal sealed class NotificationImageCache : IDisposable
{
    private const int MaxBytes = 1024 * 1024;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory = Path.Combine(AppPaths.Root, "NotificationImages");

    public NotificationImageCache() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }
    internal NotificationImageCache(HttpMessageHandler handler) => _http = new HttpClient(handler)
        { Timeout = TimeSpan.FromSeconds(1.5) };

    public async Task<string?> GetAsync(string? value, CancellationToken cancellation)
    {
        if (NotificationPolicy.ImageUrl(value) is not { } url) return null;
        try
        {
            // Decode/encode and filesystem maintenance run off the WPF dispatcher.
            return await Task.Run(async () =>
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(TimeSpan.FromSeconds(1.5));
                var token = deadline.Token;
                await _gate.WaitAsync(token);
                try
                {
                    Directory.CreateDirectory(_directory);
                    foreach (var old in new DirectoryInfo(_directory).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc)
                        .Where((f, i) => i >= 47 || f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))) old.Delete();
                    string path = Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".png");
                    if (File.Exists(path)) return path;
                    using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > MaxBytes ||
                        response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                        throw new InvalidDataException();
                    using var bytes = new MemoryStream();
                    await using var input = await response.Content.ReadAsStreamAsync(token);
                    byte[] buffer = new byte[8192];
                    int read;
                    while ((read = await input.ReadAsync(buffer, token)) != 0)
                    {
                        if (bytes.Length + read > MaxBytes) throw new InvalidDataException();
                        bytes.Write(buffer, 0, read);
                    }
                    bytes.Position = 0;
                    using var image = Image.FromStream(bytes, useEmbeddedColorManagement: false, validateImageData: true);
                    if (image.Width > 2048 || image.Height > 2048) throw new InvalidDataException();
                    using var scaled = new Bitmap(image, new Size(Math.Min(image.Width, 512), Math.Min(image.Height, 512)));
                    using var png = new MemoryStream();
                    scaled.Save(png, ImageFormat.Png);
                    if (png.Length > MaxBytes) throw new InvalidDataException();
                    token.ThrowIfCancellationRequested();
                    string temporary = path + ".tmp";
                    try
                    {
                        await File.WriteAllBytesAsync(temporary, png.ToArray(), token);
                        File.Move(temporary, path, overwrite: true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    return path;
                }
                finally { _gate.Release(); }
            }, cancellation);
        }
        catch (Exception error)
        {
            if (!cancellation.IsCancellationRequested) LoggingService.Write(LogEvent.NotificationImageFailed, error);
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
