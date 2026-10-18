using System;
using System.Diagnostics;
using System.IO;

namespace BalatroBridge.Core;

/// <summary>Starts and stops the bridged Balatro build (see tools/build_balatro.py).</summary>
public sealed class BalatroLauncher
{
    public const string BalatroSteamAppId = "2379780";

    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    public int? ProcessId => IsRunning ? _process!.Id : null;

    /// <summary>
    /// Starts Balatro pointed at the bridge on <paramref name="port"/>. With <paramref name="hidden"/>,
    /// it runs as an invisible, muted card engine and hands focus straight back to <paramref name="hostWindow"/>.
    /// </summary>
    public void Launch(string exePath, int port, bool hidden = false, long? hostWindow = null, Action<string>? log = null)
    {
        if (IsRunning) throw new InvalidOperationException($"Balatro is already running (pid {_process!.Id})");
        exePath = Path.GetFullPath(exePath); // a relative path would be resolved against Balatro's own folder
        if (!File.Exists(exePath)) throw new FileNotFoundException("Bridged Balatro build not found; run tools/build_balatro.py", exePath);

        var info = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            UseShellExecute = false,
        };
        info.Environment["BALATRO_BRIDGE_PORT"] = port.ToString();
        if (hidden) info.Environment["BALATRO_BRIDGE_HIDDEN"] = "1";
        if (hostWindow is long hwnd) info.Environment["BALATRO_BRIDGE_HOST_HWND"] = hwnd.ToString();
        // A host launched by Steam passes its own app id down to children. Without this override,
        // Balatro would initialize Steamworks as Slay the Spire 2.
        info.Environment["SteamAppId"] = BalatroSteamAppId;
        info.Environment["SteamGameId"] = BalatroSteamAppId;
        info.Environment.Remove("SteamOverlayGameId");
        _process = Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned null");
        // Backstop: if this process dies without shutting down cleanly, Windows takes Balatro with it.
        ChildProcessJob.Adopt(_process, log ?? (_ => { }));
    }

    /// <summary>Waits for a Balatro that was asked to quit. Returns true once it has exited.</summary>
    public bool WaitForExit(TimeSpan timeout) => _process == null || _process.WaitForExit(timeout);

    /// <summary>Force-closes Balatro. Prefer asking it to quit through the bridge so it can save.</summary>
    public void Kill()
    {
        if (!IsRunning) return;
        _process!.Kill();
        _process.WaitForExit(2000);
    }
}
