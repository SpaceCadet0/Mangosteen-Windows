namespace Mangosteen.Models;

/// <summary>The IS-Net model embedded in this assembly, or null in builds without it.</summary>
public static class Model
{
    public static Stream? Open() => typeof(Model).Assembly.GetManifestResourceStream("isnet.onnx");
}
