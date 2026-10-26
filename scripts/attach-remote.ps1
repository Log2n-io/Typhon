<#
.SYNOPSIS
    Opens an SSM port forward to a deployed instance and prints the endpoint to paste into the Workbench.

.DESCRIPTION
    This is W4 of claude/design/SwgTatooine/14-game-programme.md, and it exists because the recipe was a paragraph.
    A session that needed it twice got it wrong the first time and handed over a bare `localhost:9100` with no
    explanation — which reads as "the server is on your laptop" when what it means is "this is the near end of a tunnel
    to eu-west-1".

    Nothing on the box is reachable from the internet. The game port is, behind TLS; the profiler and the bot console
    are not, and deliberately: `TcpExporter` binds `IPAddress.Loopback`, has no authentication and no transport
    security, so publishing it would be publishing the engine's internals to anybody who scanned the port. AWS Systems
    Manager carries the connection instead — the instance's own role and your credentials authorise it, nothing listens
    publicly, and no security-group rule is opened.

    What comes back is a LOCAL port. That is the whole point and it is the thing to say out loud: the Workbench
    attaches to 127.0.0.1 and the bytes come from the instance.

    Three things this checks before it tries, because each one fails as something unhelpful otherwise:
      - the AWS CLI is on PATH (otherwise: "aws: not found", after the tunnel command has been printed);
      - the Session Manager plugin is installed (otherwise the CLI reports "SessionManagerPlugin is not found" and the
        session dies immediately — it is a SEPARATE install from the CLI and that catches everybody once);
      - the local port is free (otherwise the forward starts, fails to bind, and the Workbench attaches to whatever
        else happens to be on that port — which is a worse outcome than an error).

.PARAMETER InstanceId
    The instance to tunnel to, e.g. `i-0ebe4290e4a219a9f`. `terraform output server_instance_id` in
    deploy/aws/tatooine prints it, and so does `bot_instance_ids` for the bot box.

.PARAMETER Port
    The port ON THE INSTANCE. 9100 by default, which is the profiler's — the one the Workbench attaches to. Use 8090
    for the bot console.

.PARAMETER LocalPort
    The port on this machine. The same as -Port by default, so the number you read is the number you type.

.PARAMETER Region
    The instance's region. eu-west-1, which is where this stack lives (08-hosting § 1).

.PARAMETER Profile
    An AWS CLI profile, when the default credentials are not the ones for this account.

.EXAMPLE
    ./scripts/attach-remote.ps1 -InstanceId i-0ebe4290e4a219a9f
    Forwards the instance's profiler port 9100 to 127.0.0.1:9100, then attach the Workbench to 127.0.0.1:9100.

.EXAMPLE
    ./scripts/attach-remote.ps1 -InstanceId i-0e5ed97c9830d9aee -Port 8090
    The bot console, at http://127.0.0.1:8090 — which has no password, so do not forward it to an address anybody else
    can reach.

.NOTES
    The server does not profile unless it was STARTED with the profiler on: the engine bakes its telemetry gate before
    Main runs, so nothing this script does can switch it on. For a deployed box that means
    `Environment=TYPHON__PROFILER__LIVE=9100` in the unit — the same lesson as scripts/swg-serve.ps1, one machine over.
    If the tunnel opens and the Workbench cannot attach, that is the first thing to check, and `systemctl show
    tatooine -p Environment` on the box answers it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidatePattern('^i-[0-9a-f]{8,17}$')]
    [string] $InstanceId,

    [ValidateRange(1, 65535)]
    [int] $Port = 9100,

    [ValidateRange(0, 65535)]
    [int] $LocalPort = 0,

    [string] $Region = 'eu-west-1',

    [string] $Profile
)

$ErrorActionPreference = 'Stop'
if ($LocalPort -eq 0) { $LocalPort = $Port }

# The CLI. Checked rather than assumed, because the failure otherwise arrives after the banner below has told the user
# what to attach to — so they attach, it fails, and the reason has scrolled past.
if (-not (Get-Command aws -ErrorAction SilentlyContinue))
{
    throw "the AWS CLI is not on PATH. Install it from https://aws.amazon.com/cli/ — this script shells to it for the tunnel."
}

# <b>A SEPARATE install from the CLI, and this is the one everybody hits once.</b> `aws ssm start-session` without it
# fails with "SessionManagerPlugin is not found", which reads like a problem with the instance or the role rather than
# with this machine. There is no `aws` subcommand that reports its presence, so the plugin's own binary is the probe.
$pluginPaths = @(
    "$env:ProgramFiles\Amazon\SessionManagerPlugin\bin\session-manager-plugin.exe",
    "${env:ProgramFiles(x86)}\Amazon\SessionManagerPlugin\bin\session-manager-plugin.exe"
)
$hasPlugin = (Get-Command session-manager-plugin -ErrorAction SilentlyContinue) -or ($pluginPaths | Where-Object { Test-Path $_ })
if (-not $hasPlugin)
{
    throw ("the Session Manager plugin is not installed, and `aws ssm start-session` needs it as well as the CLI. " +
           "Get it from https://docs.aws.amazon.com/systems-manager/latest/userguide/session-manager-working-with-install-plugin.html")
}

# The local port, before the forward starts. A port already in use does not stop the session: it starts, fails to bind,
# and leaves the Workbench free to attach to whatever else is listening there — which looks like a successful attach to
# the wrong process, and that is a worse half hour than an error here.
$inUse = Get-NetTCPConnection -State Listen -LocalPort $LocalPort -ErrorAction SilentlyContinue
if ($inUse)
{
    $owner = (Get-Process -Id ($inUse | Select-Object -First 1).OwningProcess -ErrorAction SilentlyContinue)
    $who = if ($owner) { "$($owner.ProcessName) (pid $($owner.Id))" } else { 'another process' }
    throw "127.0.0.1:$LocalPort is already listening — $who holds it. Pick another with -LocalPort, or stop that process."
}

$common = @('--region', $Region)
if ($Profile) { $common += @('--profile', $Profile) }

# Said before the tunnel opens, because `start-session` takes over the console until it is closed.
Write-Host ''
Write-Host "  tunnel  $InstanceId`:$Port  ($Region)  ->  127.0.0.1:$LocalPort" -ForegroundColor Cyan
if ($Port -eq 9100)
{
    Write-Host "  attach the Workbench to  127.0.0.1:$LocalPort  — the near end of this tunnel, not a local server" -ForegroundColor Green
}
elseif ($Port -eq 8090)
{
    Write-Host "  open  http://127.0.0.1:$LocalPort  — the bot console, which has NO password" -ForegroundColor Yellow
}
else
{
    Write-Host "  reach it at  127.0.0.1:$LocalPort" -ForegroundColor Green
}
Write-Host '  Ctrl+C closes the tunnel. Nothing is left listening on the instance.' -ForegroundColor DarkGray
Write-Host ''

# AWS-StartPortForwardingSession rather than a shell: the instance role grants no shell to this path, and a forward is
# all that is wanted. The parameter document is JSON on one line because the CLI's own --parameters parser takes it
# that way on every platform, where the shorthand form quotes differently per shell.
$parameters = "{`"portNumber`":[`"$Port`"],`"localPortNumber`":[`"$LocalPort`"]}"

& aws ssm start-session `
    --target $InstanceId `
    --document-name AWS-StartPortForwardingSession `
    --parameters $parameters `
    @common

# Not $ErrorActionPreference's business: a native command's exit code does not throw. 130 is Ctrl+C, which is how this
# script is MEANT to end, so it is not a failure to report.
if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 130)
{
    # Single-quoted tail, concatenated: a backtick inside a double-quoted string is PowerShell's escape character, so the
    # markdown-style `aws ssm ...` in this message silently lost its first letter and advised running "ws ssm ...".
    throw "aws ssm start-session exited with $LASTEXITCODE. A stopped instance, an instance with no SSM agent registered, " +
          "or credentials for another account all land here. 'aws ssm describe-instance-information --region $Region' " +
          'lists what SSM can actually see.'
}
