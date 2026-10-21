using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodeIsland.Core;
using Image = System.Windows.Controls.Image;

namespace CodeIsland.Windows.Island;

/// Pixel mascot from the macOS island. GDI+ composites each GIF frame (disposal and
/// partial frames), which WPF's GifBitmapDecoder does not — raw frames show as torn lines.
public sealed class Mascot : Image
{
    private sealed record GifClip(BitmapSource[] Frames, TimeSpan[] Delays);

    private static readonly ConcurrentDictionary<string, GifClip?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly long Epoch = Environment.TickCount64;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly GifClip? _clip;
    private int _frame = -1;

    public Mascot(AgentKind agent, double size, bool animate)
    {
        Width = size;
        Height = size;
        Stretch = Stretch.Uniform;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        _clip = Load(PathFor(agent));
        Source = _clip?.Frames[0];
        if (animate && _clip is { Frames.Length: > 1 })
        {
            ShowCurrentFrame();
            _timer.Tick += (_, _) => ShowCurrentFrame();
            Loaded += (_, _) => _timer.Start();
            Unloaded += (_, _) => _timer.Stop();
        }
    }

    /// The frame comes from one shared clock, so a mascot rebuilt on every island
    /// redraw carries on mid-loop instead of restarting at frame 0.
    private void ShowCurrentFrame()
    {
        var clip = _clip!;
        var cycle = clip.Delays.Sum(d => d.TotalMilliseconds);
        var t = (Environment.TickCount64 - Epoch) % cycle;
        var index = 0;
        for (; index < clip.Delays.Length - 1; index++)
        {
            t -= clip.Delays[index].TotalMilliseconds;
            if (t < 0) break;
        }
        if (index == _frame) return;
        _frame = index;
        Source = clip.Frames[index];
    }

    public static string PathFor(AgentKind agent)
    {
        var name = agent switch
        {
            AgentKind.Claude => "claude", AgentKind.Codex => "codex", AgentKind.Gemini => "gemini",
            AgentKind.Cursor => "cursor", AgentKind.Copilot => "copilot", AgentKind.Trae => "trae",
            AgentKind.Qoder => "qoder", AgentKind.Factory => "factory", AgentKind.CodeBuddy => "codebuddy",
            AgentKind.OpenCode => "opencode", AgentKind.Kimi => "kimi", AgentKind.Cline => "cline",
            AgentKind.Pi => "pi", _ => "claude"
        };
        return System.IO.Path.Combine(AppContext.BaseDirectory, "source", "mascots", name + ".gif");
    }

    private static GifClip? Load(string path) => Cache.GetOrAdd(path, static file =>
    {
        if (!File.Exists(file)) return null;
        try
        {
            using var gif = System.Drawing.Image.FromFile(file);
            var dimension = new FrameDimension(gif.FrameDimensionsList[0]);
            var count = gif.GetFrameCount(dimension);
            var delays = ReadDelays(gif, count);
            var frames = new BitmapSource[count];
            for (var i = 0; i < count; i++)
            {
                gif.SelectActiveFrame(dimension, i);
                using var frame = new System.Drawing.Bitmap(gif.Width, gif.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = System.Drawing.Graphics.FromImage(frame)) g.DrawImage(gif, 0, 0, gif.Width, gif.Height);
                frames[i] = ToBitmapSource(frame);
            }
            return new GifClip(frames, delays);
        }
        catch (Exception ex) when (ex is IOException or OutOfMemoryException or ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    });

    private static TimeSpan[] ReadDelays(System.Drawing.Image gif, int count)
    {
        var delays = Enumerable.Repeat(TimeSpan.FromMilliseconds(100), count).ToArray();
        try
        {
            var item = gif.GetPropertyItem(0x5100);
            if (item?.Value is { } bytes)
                for (var i = 0; i < count && i * 4 + 3 < bytes.Length; i++)
                {
                    var hundredths = BitConverter.ToInt32(bytes, i * 4);
                    delays[i] = TimeSpan.FromMilliseconds(Math.Max(20, hundredths * 10));
                }
        }
        catch (ArgumentException) { }
        return delays;
    }

    private static BitmapSource ToBitmapSource(System.Drawing.Bitmap bitmap)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgra32, null,
                data.Scan0, data.Stride * bitmap.Height, data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}