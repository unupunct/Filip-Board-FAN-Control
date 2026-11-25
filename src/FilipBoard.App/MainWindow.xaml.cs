using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FilipBoard.App;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            // Dark title bar to match the theme (Windows 10 20H1+/11); ignored if unsupported.
            var h = new WindowInteropHelper(this).Handle; var on = 1;
            DwmSetWindowAttribute(h, 20, ref on, sizeof(int));
        };
    }
}
