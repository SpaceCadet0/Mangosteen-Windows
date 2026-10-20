namespace Mangosteen.Windows.Tests;

/// <summary>Deletes a test folder, retrying while an antivirus or indexer still holds a new file.</summary>
internal static class TempCleanup
{
    public static void Delete(string folder)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }
}
