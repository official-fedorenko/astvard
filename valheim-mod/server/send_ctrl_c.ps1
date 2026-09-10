param([int]$TargetPid)

# Presses CTRL+C in the console a process lives in - for the dedicated server, which runs in
# a hidden window. On CTRL+C the game quits properly and writes the world on the way out
# (Game - OnApplicationQuit, ZNet Shutdown, World save (5/5) done); a killed server loses
# everything since its last autosave.
#
# Start it as its own hidden process: it has to give up its own console to borrow the
# server's, which the shell calling it would not survive.
$sig = @'
using System;
using System.Runtime.InteropServices;
public static class ConsoleCtrl {
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GenerateConsoleCtrlEvent(uint ev, uint group);
}
'@
Add-Type -TypeDefinition $sig

[ConsoleCtrl]::FreeConsole() | Out-Null
if (-not [ConsoleCtrl]::AttachConsole([uint32]$TargetPid)) { exit 2 }

# Ignore the event ourselves; everything else on that console gets it.
[ConsoleCtrl]::SetConsoleCtrlHandler([IntPtr]::Zero, $true) | Out-Null
$sent = [ConsoleCtrl]::GenerateConsoleCtrlEvent(0, 0)
Start-Sleep -Milliseconds 500
[ConsoleCtrl]::FreeConsole() | Out-Null

if ($sent) { exit 0 } else { exit 3 }
