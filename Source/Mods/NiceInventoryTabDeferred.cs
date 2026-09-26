using System.Diagnostics;
using Multiplayer.API;
using Multiplayer.Compat;
using RimWorld;
using Verse;

namespace MultiplayerNiceInventoryTabPatch.Source.Mods;

public partial class NiceInventoryTab
{
    #region Deferred orders (float-menu wrapper escape hatch)

    // MP wraps FloatMenuOption choices in a sync scope on the clicking client but can
    // only replay them on remotes for registered sync delegates. NIT builds custom
    // FloatMenus with unregistered compiler-generated lambdas, so a wrapped click
    // would run clicker-only. Nested synced calls cannot broadcast (MP runs them
    // directly), so flagged clicks are deferred here and broadcast from unflagged
    // interface context on the next UI draw (a frame later, imperceptible).

    private static readonly Queue<(int dueTick, Action run)> deferredOrders = new();
    private const int DeferredOrderTtlTicks = 300;

    private static void EnqueueOrder(Action run)
    {
        if (run == null) return;
        deferredOrders.Enqueue((Find.TickManager.TicksGame, run));
    }

    private static void DrainOrders()
    {
        if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand || !MP.InInterface) return;

        while (deferredOrders.Count > 0)
        {
            var (dueTick, run) = deferredOrders.Dequeue();
            if (Find.TickManager.TicksGame - dueTick > DeferredOrderTtlTicks) continue;
            try
            {
                run();
            }
            catch (Exception e)
            {
                Log.Warning($"{LogPrefix} Deferred order failed, skipping: {e.GetType().Name}");
            }
        }
    }

    // Primary drain: the gear tab redraws every frame while open, which covers every
    // order taken from it (all NIT menus spawn from the tab).
    [MpCompatPrefix(typeof(ITab_Pawn_Gear), "FillTab")]
    private static void DrainHook_GearTab()
    {
        DrainOrders();
    }

    // Backstop drain: covers orders taken while the tab is closed (menu left open).
    // No-arg prefixes always bind; DrainOrders re-validates context itself.
    [MpCompatPrefix(typeof(FloatMenu), "DoWindowContents")]
    private static void DrainHook_FloatMenu()
    {
        DrainOrders();
    }

    private const string NitNamespaceMarker = "NiceInventoryTab.";
    private const string OwnNamespaceMarker = "MultiplayerNiceInventoryTabPatch";

    // True when the current call stack passes through NIT's own UI code (menu lambdas,
    // row widgets). Our compat frames are excluded so they never self-match. Used to
    // tell MP-wrapped NIT menu clicks apart from MP replays of vanilla menus.
    private static bool StackHasNitUi()
    {
        foreach (var frame in new StackTrace().GetFrames() ?? Array.Empty<StackFrame>())
        {
            var name = frame.GetMethod()?.DeclaringType?.FullName;
            if (name != null && name.Contains(NitNamespaceMarker) && !name.Contains(OwnNamespaceMarker))
                return true;
        }

        return false;
    }

    // True when the current call stack passes through one of our own synced workers
    // (used to let their direct vanilla calls through instead of re-syncing them).
    private static bool StackHasOwnSynced(string syncedMethodName)
    {
        foreach (var frame in new StackTrace().GetFrames() ?? Array.Empty<StackFrame>())
        {
            var method = frame.GetMethod();
            var typeName = method?.DeclaringType?.FullName;
            if (typeName != null && typeName.Contains(OwnNamespaceMarker) && method.Name == syncedMethodName)
                return true;
        }

        return false;
    }

    #endregion
}