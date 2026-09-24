using System.Text;
using System.IO;

namespace HdrCapture.Infrastructure;

internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string DirectoryPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HDRCapture");
    private static readonly string LogPath = Path.Combine(DirectoryPath, "HDRCapture.log");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var builder = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" [")
                    .Append(level)
                    .Append("] ")
                    .Append(message);
                if (exception is not null)
                {
                    builder.AppendLine().Append(exception);
                }

                File.AppendAllText(LogPath, builder.AppendLine().ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never interfere with capture.
        }
    }
}
