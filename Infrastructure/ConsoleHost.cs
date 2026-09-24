using System.Runtime.InteropServices;
using System.Text;
using System.IO;

namespace HdrCapture.Infrastructure;

internal sealed class ConsoleHost : IDisposable
{
    private const int AttachParentProcess = -1;
    private readonly bool _attached;

    private ConsoleHost(bool attached)
    {
        _attached = attached;
    }

    public bool IsAttached => _attached;

    public static ConsoleHost TryAttach()
    {
        var attached = AttachConsole(AttachParentProcess);
        if (attached)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }

        return new ConsoleHost(attached);
    }

    public void WriteLine(string text)
    {
        if (_attached)
        {
            Console.WriteLine(text);
        }
    }

    public void Dispose()
    {
        if (_attached)
        {
            FreeConsole();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();
}
