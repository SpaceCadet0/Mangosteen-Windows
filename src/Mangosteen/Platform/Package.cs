using System.Runtime.InteropServices;

namespace Mangosteen.Platform;

/// <summary>Whether Mangosteen runs from its MSIX package (the Microsoft Store build) or as the plain exe.</summary>
internal static class Package
{
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, char[]? name);

    public static bool IsPackaged { get; } = Detect();

    private static bool Detect()
    {
        try
        {
            var length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
