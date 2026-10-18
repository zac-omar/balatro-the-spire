using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using BalatroBridge.Cards;
using BalatroBridge.Content;
using BalatroBridge.Core;
using BalatroBridge.Jimbo;
using BalatroBridge.Relics;
using BalatroBridge.Table;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models.CardPools;

namespace BalatroBridge;

/// <summary>
/// Mod entry point, called once by Slay the Spire 2's ModManager during startup.
/// Starts the bridge server and pumps it every frame on the Godot main thread, so all bridge
/// events can safely touch game state.
/// </summary>
[ModInitializer(nameof(Initialize))]
public static class BridgeMod
{
    public const string ModId = "BalatroBridge";

    private static readonly TimeSpan QuitGracePeriod = TimeSpan.FromSeconds(2);

    private static BridgeServer? _server;
    private static bool _shutDown;

    public static BridgeConfig Config { get; private set; } = new();

    /// <summary>The conversation with Balatro; null if the bridge failed to start.</summary>
    public static BalatroSession? Session { get; private set; }

    public static BalatroLauncher Launcher { get; } = new();

    private static void Initialize()
    {
        // Content and patches go in first: pools freeze and loc tables load right after mod init.
        // Jimbo's relics and potions (every Balatro item) live in his own pools, which StS2 never
        // rolls from: only Balatro hands them out.
        ModHelper.AddModelToPool<ColorlessCardPool, BalatroDealIn>();
        new Harmony(ModId).PatchAll(typeof(BridgeMod).Assembly);

        string modDir = Path.GetDirectoryName(typeof(BridgeMod).Assembly.Location)!;
        Config = BridgeConfig.Load(Path.Combine(modDir, BridgeConfig.FileName), LogInfo);
        BalatroCatalog.LoadSaved();
        BalatroCatalog.Changed += ModText.Refresh;
        // Decode Balatro's art, build the decks' and stakes' icons and load Balatro's music and
        // sounds now, off the game thread, instead of stalling frames the first time they're used.
        // Potions are looked up by a fixed path, so their icons are put there once the art is in.
        BalatroSheets.Preload(then: () =>
        {
            BalatroIcons.Prepare(BalatroContent.All.Where(i => i.Set is "Back" or "Stake" || i.IsConsumable));
            BalatroAudio.Preload(TableScreen.Sounds.Concat(JimboSounds.Sounds));
            Callable.From(BalatroIcons.PlacePotionIcons).CallDeferred();
        });

        _server = new BridgeServer(Config.Port, LogInfo);
        try
        {
            _server.Start();
        }
        catch (SocketException e)
        {
            LogError($"could not listen on port {Config.Port} ({e.Message}); is another copy of the game running?");
            return;
        }

        Session = new BalatroSession(_server, ReleaseInfoManager.Instance.SemVer?.ToString() ?? "unknown", LogInfo);
        Session.GameEvent += (name, data) => LogInfo($"Balatro event '{name}': {data.ToJsonString()}");
        Session.Ready += () => TaskHelper.RunSafely(BalatroCatalog.Refresh(Session));
        CombatManager.Instance.CombatSetUp += PrepareBalatro;

        if (Engine.GetMainLoop() is SceneTree tree)
        {
            tree.ProcessFrame += Session.Pump;
            tree.ProcessFrame += JimboMusic.Update;
            tree.ProcessFrame += JimboRun.Watch;
            // The window's close button quits through Godot directly, skipping NGame.Quit.
            tree.Root.TreeExiting += () => Shutdown("game window closing");
        }
        else
        {
            LogError("no SceneTree to pump the bridge from; Balatro messages will not be processed");
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown("process exit");

        if (Config.AutoLaunch) TryLaunchBalatro(out _);
        LogInfo($"bridge {BalatroSession.BridgeVersion} initialized");
    }

    public static bool TryLaunchBalatro(out string message)
    {
        if (string.IsNullOrWhiteSpace(Config.BalatroExe))
        {
            message = $"balatro_exe is not set in {BridgeConfig.FileName}";
            return false;
        }
        try
        {
            long hostWindow = DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle);
            Launcher.Launch(Config.BalatroExe, Config.Port, Config.Hidden, hostWindow, LogError);
            message = $"launched {Config.BalatroExe} (pid {Launcher.ProcessId})";
            LogInfo(message);
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or Win32Exception)
        {
            message = $"could not launch Balatro: {e.Message}";
            LogError(message);
            return false;
        }
    }

    /// <summary>
    /// Closes everything the mod started: asks Balatro to quit (so it saves), force-closes it if it
    /// doesn't go quickly, and stops the bridge server. Runs once, from whichever exit path fires first.
    /// If StS2 dies without getting here, the job object in BalatroLauncher closes Balatro anyway.
    /// </summary>
    internal static void Shutdown(string reason)
    {
        if (_shutDown) return;
        _shutDown = true;
        LogInfo($"shutting down ({reason})");
        try
        {
            if (Launcher.IsRunning)
            {
                if (Session is { IsReady: true }) Session.Send("quit");
                if (!Launcher.WaitForExit(QuitGracePeriod))
                {
                    LogInfo("Balatro didn't quit in time; closing it");
                    Launcher.Kill();
                }
            }
            _server?.Dispose();
        }
        catch (Exception e)
        {
            LogError($"shutdown problem: {e.Message}");
        }
    }

    /// <summary>
    /// As a fight is set up, tells Balatro which run's hand to have ready, so the fight's first
    /// Deal In opens at once instead of waiting for Balatro to load the run.
    /// </summary>
    private static void PrepareBalatro(CombatState state)
    {
        try
        {
            if (Session is not { IsReady: true } session || JimboRun.IsJimbo(LocalContext.GetMe(state))) return;
            if (DealInRelics.LinkFor(LocalContext.GetMe(state)) is TableLink link) session.LinkTable(link);
        }
        catch (Exception e)
        {
            LogError($"couldn't prepare Balatro for the fight: {e.Message}");
        }
    }

    internal static void LogInfo(string message) => Log.Info($"[{ModId}] {message}");

    internal static void LogError(string message) => Log.Error($"[{ModId}] {message}");
}
