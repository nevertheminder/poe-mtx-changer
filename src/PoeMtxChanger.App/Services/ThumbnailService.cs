using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using PoeMtxChanger.Core.Formats.Dds;
using PoeMtxChanger.Core.Storage;

namespace PoeMtxChanger.App.Services;

/// <summary>Decodes item icons (.dds) from the user's installation on a background thread, downscaled and cached.</summary>
public sealed class ThumbnailService(AppServices app)
{
    private const int MaxSize = 256;
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(2);
    private readonly ILogger _log = app.Log("Thumbnails");
    private IResourceSource? _mockSource;
    private object? _sourceKey;

    public Task<ImageSource?> GetAsync(string path)
    {
        var source = CurrentSource();
        if (source is null) return Task.FromResult<ImageSource?>(null);
        if (!ReferenceEquals(source, _sourceKey)) { _cache.Clear(); _sourceKey = source; }
        return _cache.GetOrAdd(path, p => LoadAsync(source, p));
    }

    private IResourceSource? CurrentSource()
    {
        if (app.Game.Archive is { } a) return a;
        if (app.Game.CatalogIsMock) return _mockSource ??= Core.Catalog.MockCatalog.CreateIconSource(app.Game.Catalog);
        return null;
    }

    private async Task<ImageSource?> LoadAsync(IResourceSource source, string path)
    {
        await _gate.WaitAsync();
        try
        {
            return await Task.Run<ImageSource?>(() =>
            {
                if (!source.Exists(path)) return null;
                var img = DdsDecoder.Decode(source.Read(path), p => source.Exists(p) ? source.Read(p) : null);
                var (w, h, px) = Downscale(img);
                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
                bmp.Freeze();
                return bmp;
            });
        }
        catch (Exception ex) when (ex is DdsNotSupportedException or InvalidDataException or IOException or InvalidOperationException)
        {
            _log.LogDebug("No thumbnail for {Path}: {Reason}", path, ex.Message);
            return null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Box filter down to at most <see cref="MaxSize"/> on the longer side.</summary>
    private static (int W, int H, byte[] Px) Downscale(DecodedImage img)
    {
        int f = 1;
        while (Math.Max(img.Width, img.Height) / f > MaxSize) f *= 2;
        if (f == 1) return (img.Width, img.Height, img.Bgra);
        int w = Math.Max(1, img.Width / f), h = Math.Max(1, img.Height / f);
        var o = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0;
                    for (int yy = 0; yy < f; yy++)
                        for (int xx = 0; xx < f; xx++)
                            sum += img.Bgra[((y * f + yy) * img.Width + (x * f + xx)) * 4 + c];
                    o[(y * w + x) * 4 + c] = (byte)(sum / (f * f));
                }
        return (w, h, o);
    }
}
