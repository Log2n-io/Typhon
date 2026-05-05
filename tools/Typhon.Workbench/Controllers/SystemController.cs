using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// System-introspection endpoints used by the client to render OS-aware UI (e.g., disabling
/// "Visual Studio" in the editor dropdown on macOS).
/// </summary>
[ApiController]
[Route("api/system")]
public sealed class SystemController : ControllerBase
{
    /// <summary>Return the running OS as a stable lowercase string: "windows" | "macos" | "linux" | "other".</summary>
    [HttpGet("os")]
    public ActionResult<OsInfo> GetOs()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Ok(new OsInfo("windows"));
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Ok(new OsInfo("macos"));
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return Ok(new OsInfo("linux"));
        }
        return Ok(new OsInfo("other"));
    }

    public sealed record OsInfo(string Os);
}
