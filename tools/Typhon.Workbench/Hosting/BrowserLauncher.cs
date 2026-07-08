using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Typhon.Workbench.Hosting;

/// <summary>
/// Opens the user's default browser at a URL, cross-platform. Used by <c>typhon ui</c> to launch the Workbench
/// SPA once Kestrel is listening. Best-effort: a headless machine or a missing browser must never crash the host,
/// so failures are reported (not thrown) and the caller prints the URL for manual opening.
/// </summary>
public static class BrowserLauncher
{
    /// <summary>Attempts to open <paramref name="url"/> in the default browser. Returns false if the launch failed.</summary>
    public static bool TryOpen(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // UseShellExecute lets the OS pick the default browser for the http(s) scheme.
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                Process.Start("xdg-open", url);
            }

            return true;
        }
        catch (Exception)
        {
            // No browser / headless / sandboxed launcher — the caller surfaces the URL for manual opening.
            return false;
        }
    }
}
