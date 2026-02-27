using System.Runtime.InteropServices;

namespace TalkTranscript;

/// <summary>
/// コンソールウィンドウのフォントを Win32 API で変更するヘルパー。
/// Windows Terminal では無視される (例外は握りつぶす)。
/// </summary>
internal static class ConsoleHelper
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetCurrentConsoleFontEx(
        IntPtr hConsoleOutput, bool bMaximumWindow,
        ref CONSOLE_FONT_INFOEX lpConsoleCurrentFontEx);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CONSOLE_FONT_INFOEX
    {
        public uint cbSize;
        public uint nFont;
        public short dwFontSizeX;
        public short dwFontSizeY;
        public int FontFamily;
        public int FontWeight;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FaceName;
    }

    public static void SetFont(string fontName, short fontSize)
    {
        try
        {
            const int STD_OUTPUT_HANDLE = -11;
            var handle = GetStdHandle(STD_OUTPUT_HANDLE);

            var info = new CONSOLE_FONT_INFOEX
            {
                cbSize = (uint)Marshal.SizeOf<CONSOLE_FONT_INFOEX>(),
                nFont = 0,
                dwFontSizeX = 0,
                dwFontSizeY = fontSize,
                FontFamily = 54,   // FF_MODERN | FIXED_PITCH
                FontWeight = 400,  // FW_NORMAL
                FaceName = fontName
            };

            SetCurrentConsoleFontEx(handle, false, ref info);
        }
        catch
        {
            // Windows Terminal 等では非対応 — 無視
        }
    }
}

/// <summary>
/// コンソールにスピナーアニメーションを表示する。
/// Dispose() で停止・行クリアする。
/// </summary>
internal sealed class ConsoleSpinner : IDisposable
{
    private static readonly string[] Frames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
    private readonly string _message;
    private readonly Thread _thread;
    private volatile bool _stopped;
    private int _lastLen;

    public ConsoleSpinner(string message)
    {
        _message = message;
        _thread = new Thread(Spin) { IsBackground = true };
        _thread.Start();
    }

    private void Spin()
    {
        int i = 0;
        try
        {
            while (!_stopped)
            {
                var text = $"\r  {Frames[i % Frames.Length]} {_message}";
                _lastLen = text.Length;
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.Write(text);
                Console.ResetColor();
                Thread.Sleep(80);
                i++;
            }
        }
        catch { /* console disposed etc. */ }
    }

    public void Dispose()
    {
        if (_stopped) return;
        _stopped = true;
        _thread.Join(2000);
        try
        {
            Console.Write("\r" + new string(' ', Math.Max(_lastLen, 1)) + "\r");
        }
        catch { }
    }
}
