# Captures ONLY the app's own window (PrintWindow), never the full screen.
param([string]$Exe, [string]$ArgsLine = "", [string]$Out, [int]$WaitSeconds = 7)
Add-Type -ReferencedAssemblies System.Drawing @"
using System; using System.Runtime.InteropServices; using System.Drawing; using System.Drawing.Imaging;
public static class WinCap {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [StructLayout(LayoutKind.Sequential)] struct RECT { public int L,T,R,B; }
  public static void Save(IntPtr h, string path) {
    RECT r; GetWindowRect(h, out r); int w=r.R-r.L, ht=r.B-r.T;
    using (var bmp = new Bitmap(w, ht)) { using (var g = Graphics.FromImage(bmp)) { IntPtr dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); } bmp.Save(path, ImageFormat.Png); }
  }
}
"@
[WinCap]::SetProcessDPIAware() | Out-Null
$p = Start-Process -FilePath $Exe -ArgumentList $ArgsLine -PassThru
Start-Sleep -Seconds $WaitSeconds
$p.Refresh()
if ($p.MainWindowHandle -eq 0) { Write-Error "No window"; $p.Kill(); exit 1 }
[WinCap]::Save($p.MainWindowHandle, $Out)
$p.CloseMainWindow() | Out-Null; Start-Sleep 1; if (!$p.HasExited) { $p.Kill() }
"saved $Out"
