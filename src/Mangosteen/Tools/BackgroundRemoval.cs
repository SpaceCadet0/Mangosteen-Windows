using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mangosteen.Core.Imaging;
using Mangosteen.Platform;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Mangosteen.Tools;

/// <summary>
/// Finds the subject of a photo with the bundled IS-Net model (ONNX Runtime on the processor, no network). The
/// model is loaded on first use and let go a minute after the last one, so it costs no memory in the background.
/// </summary>
internal static class BackgroundRemoval
{
    private static readonly object Gate = new();
    private static InferenceSession? _session;
    private static Timer? _release;

    /// <summary>True when this build carries the model (build/fetch-isnet.ps1 ran before it was built).</summary>
    public static bool IsBundled { get; } = typeof(BackgroundRemoval).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(a => a.Key == "IsnetHash" && !string.IsNullOrEmpty(a.Value));

    /// <summary>The model's mask, <see cref="SubjectMask.Side"/> pixels square (0 background … 255 subject).</summary>
    public static byte[] Mask(BitmapSource image)
    {
        var input = SubjectMask.Input(ImageIO.ToBgra(Sta.Run(() => ImageIO.Resize(image, SubjectMask.Side, SubjectMask.Side))));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        lock (Gate)
        {
            var session = Session();
            var tensor = new DenseTensor<float>(input, [1, 3, SubjectMask.Side, SubjectMask.Side]);
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), tensor)]);
            var output = results.First().AsTensor<float>().ToArray();
            Log.Write($"background removal: {clock.ElapsedMilliseconds} ms");
            _release?.Change(TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);
            return SubjectMask.Normalize(output);
        }
    }

    /// <summary>The mask stretched to the image's size (smooth edges).</summary>
    public static byte[] Scale(byte[] mask, int width, int height)
    {
        var side = SubjectMask.Side;
        var gray = BitmapSource.Create(side, side, 96, 96, PixelFormats.Gray8, null, mask, side);
        gray.Freeze();
        var scaled = ImageIO.ToBgra(Sta.Run(() => ImageIO.Resize(gray, width, height)));
        var result = new byte[width * height];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = scaled.Pixels[i * 4];
        }
        return result;
    }

    private static InferenceSession Session()
    {
        if (_session is { } session)
        {
            return session;
        }
        using var stream = Models.Model.Open() ?? throw new InvalidOperationException("This build has no background-removal model.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        _session = new InferenceSession(memory.ToArray(), new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL });
        _release ??= new Timer(_ => Release(), null, Timeout.Infinite, Timeout.Infinite);
        _release.Change(TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);
        Log.Write("background removal: model loaded");
        return _session;
    }

    private static void Release()
    {
        lock (Gate)
        {
            _session?.Dispose();
            _session = null;
        }
        GC.Collect();
        Log.Write("background removal: model released");
    }
}
