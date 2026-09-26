using Multiplayer.API;
using Multiplayer.Compat;
using RimWorld;
using Verse;

namespace MultiplayerNiceInventoryTabPatch.Source.Mods;

public partial class NiceInventoryTab
{
    #region Vanilla interop patches (vanilla/RimWorld methods NIT drives)

    // Unlike the NIT command patches above, these wrap vanilla methods that run in
    // more than one context (UI clicks AND tick logic AND our own synced bodies), so
    // each prefix discriminates explicitly instead of relying on the flag alone.

    // Forced-wear toggle. Uses __args to stay independent of vanilla parameter names;
    // the pawn is resolved by handler identity (see NiceInventoryTabLookups). Covers
    // every NIT call site (row lock buttons, right-click float menu, apparel grid).
    // Four-way rule: tick calls run vanilla (deterministic on all clients); plain UI
    // clicks broadcast now; MP-wrapped menu clicks (NIT frames on the stack) are
    // deferred; our own synced execution (own frame on the stack) runs vanilla.
    // The stack walks are load-bearing: SetForced, unlike the CommandUtility methods,
    // is also called on tick (vanilla Wear finish, Layered Apparel, CE) and by
    // our own SyncedSetForced body, so a bare IsExecutingSyncCommand check can neither
    // recurse (proven StackOverflow) nor silently stay local.
    [MpCompatPrefix(typeof(OutfitForcedHandler), nameof(OutfitForcedHandler.SetForced))]
    private static bool Prefix_SetForced(object __instance, object[] __args)
    {
        if (!MP.IsInMultiplayer || !MP.InInterface) return true;
        if (__args == null || __args.Length < 2) return true;

        var apparel = __args[0] as Apparel;
        if (apparel == null || __args[1] is not bool forced) return true;

        // Our own broadcast executing: let the vanilla call through.
        if (StackHasOwnSynced(nameof(SyncedSetForced))) return true;

        var pawn = PawnOfForcedHandler(__instance);
        if (pawn == null)
        {
            Log.Warning($"{LogPrefix} Could not resolve pawn for forced-wear toggle - running locally, may desync.");
            return true;
        }

        if (MP.IsExecutingSyncCommand)
        {
            // MP-wrapped NIT menu click (proven by stack): defer, broadcast later.
            if (StackHasNitUi())
                EnqueueOrder(() => SyncedSetForced(pawn, apparel, forced));
            else
                return true;
        }
        else
        {
            SyncedSetForced(pawn, apparel, forced);
        }

        return false;
    }

    // Ingest-from-inventory buttons. MP core already syncs this vanilla method (do NOT
    // register it again - it would double-sync), so the drain path simply calls the
    // vanilla method and lets the core broadcast it. Same four-way shape as SetForced:
    // NIT menu clicks are deferred, everything else runs vanilla and lets MP handle it.
    [MpCompatPrefix(typeof(FoodUtility), "IngestFromInventoryNow", new[] { typeof(Pawn), typeof(Thing) })]
    private static bool Prefix_Ingest(object[] __args)
    {
        if (!MP.IsInMultiplayer || !MP.InInterface) return true;
        if (__args == null || __args.Length < 2) return true;

        var pawn = __args[0] as Pawn;
        var item = __args[1] as Thing;
        if (pawn == null || item == null) return true;

        if (MP.IsExecutingSyncCommand)
        {
            // MP-wrapped NIT menu click: cancel now, the deferred broadcast replays it
            // on all clients (the vanilla call would otherwise run clicker-only).
            if (StackHasNitUi())
            {
                EnqueueOrder(() => FoodUtility.IngestFromInventoryNow(pawn, item));
                return false;
            }

            return true;
        }

        return true;
    }

    // Vanilla StatPart_NoxiousHaze assumes every inspected thing resolves to a map
    // position and throws NullReferenceException otherwise (e.g. apparel on a corpse's
    // InnerPawn, which NIT displays). That aborts Dialog_InfoCard on both clients.
    // The haze stat is meaningless without a map, so report inactive there instead.
    // String-based target: if vanilla ever renames it, only this patch logs an error
    // instead of breaking compilation.
    [MpCompatPrefix("RimWorld.StatPart_NoxiousHaze", "ActiveFor")]
    private static bool Prefix_NoxiousHazeActive(object[] __args, ref bool __result)
    {
        if (__args == null || __args.Length < 1) return true;
        if (__args[0] is not Thing thing || thing == null)
        {
            __result = false;
            return false;
        }

        Map map = null;
        try
        {
            map = thing.MapHeld;
        }
        catch
        {
            // Treat unresolvable holders the same as mapless below.
        }

        if (map == null)
        {
            __result = false;
            return false;
        }

        return true;
    }

    #endregion
}