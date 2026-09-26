using Multiplayer.API;
using Multiplayer.Compat;
using NiceInventoryTab;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace MultiplayerNiceInventoryTabPatch.Source.Mods;

public partial class NiceInventoryTab
{
    #region NIT command patches (UI thread on the acting client -> synced workers)

    // NOTE: Harmony binds patch parameters by NAME, so these must exactly match
    // the original method's parameter names (verified against the decompiled assembly).
    //
    // MP wraps FloatMenuOption choices in a sync scope on the clicking client but can
    // only replay them on remotes for registered sync delegates. NIT builds custom
    // FloatMenus with unregistered lambdas, so a wrapped click would run clicker-only
    // (proven by host-only Prefix_CommandWear with SyncedCommandWear never reached).
    // Nested synced calls cannot broadcast, so flagged clicks are deferred to the next
    // UI draw (see NiceInventoryTabDeferred) where they broadcast normally. Our synced
    // bodies never call back into these methods, so flagged here always means the MP
    // wrapper.

    [MpCompatPrefix(typeof(CommandUtility), nameof(CommandUtility.CommandCreate))]
    private static bool Prefix_CommandCreate(
        Building_WorkTable wtable,
        ThingDef appdef,
        RecipeDef recipe,
        ApparelLayerDef apparelLayer)
    {
        if (!MP.IsInMultiplayer || !MP.InInterface) return true;
        if (wtable?.billStack == null || recipe == null || appdef == null) return false;

        // The original reads the UI static lastPawn internally; capture it on the
        // acting client and send it explicitly so remotes use the right pawn.
        var target = ITab_Pawn_Gear_Patch.lastPawn;
        if (target == null) return false;

        if (MP.IsExecutingSyncCommand)
            EnqueueOrder(() => SyncedCommandCreate(wtable, appdef, recipe, apparelLayer, target));
        else
            SyncedCommandCreate(wtable, appdef, recipe, apparelLayer, target);

        // Local-only UI follow-up, mirrors the original tail.
        Messages.Message(
            "NIT_BillCreatedExpl".Translate(wtable.LabelCap, target.NameShortColored),
            MessageTypeDefOf.TaskCompletion, false);
        if (wtable.Map == Find.CurrentMap)
            CameraJumper.TryJump((GlobalTargetInfo)(Thing)wtable);

        return false;
    }

    [MpCompatPrefix(typeof(CommandUtility), nameof(CommandUtility.CommandWear))]
    private static bool Prefix_CommandWear(Pawn pawn, Thing app)
    {
        if (!MP.IsInMultiplayer || !MP.InInterface) return true;
        if (pawn?.jobs == null || app == null) return false;

        // TEMP DIAGNOSTIC - will be removed once the intermittent Wear desync is root-caused.
        Log.Message(
            $"{LogPrefix} DIAG Wear order: exec={MP.IsExecutingSyncCommand} pawn={pawn.LabelShort} app={app.def?.defName} spawned={app.Spawned} forbidden={app.IsForbidden(pawn)}");

        // Def + position travel alongside for the ID-mismatch fallback (see worker).
        // Captured now: by drain time the item may be gone and Position unreadable.
        var appDef = app.def;
        var appPos = app.Position;
        if (MP.IsExecutingSyncCommand)
            EnqueueOrder(() => SyncedCommandWear(pawn, app, appDef, appPos));
        else
            SyncedCommandWear(pawn, app, appDef, appPos);

        if (pawn.Map == Find.CurrentMap)
            CameraJumper.TryJump((GlobalTargetInfo)app);

        return false;
    }

    [MpCompatPrefix(typeof(CommandUtility), nameof(CommandUtility.CommandRemoveToInventory))]
    private static bool Prefix_CommandRemoveToInventory(Pawn pawn, Thing app, bool start)
    {
        if (!MP.IsInMultiplayer || !MP.InInterface) return true;
        if (pawn?.jobs == null || app == null) return false;

        if (MP.IsExecutingSyncCommand)
            EnqueueOrder(() => SyncedRemoveToInventory(pawn, app, start));
        else
            SyncedRemoveToInventory(pawn, app, start);
        return false;
    }

    [MpCompatPrefix(typeof(CommandUtility), nameof(CommandUtility.CommandWearFromInventory))]
    private static bool Prefix_CommandWearFromInventory(Pawn pawn, Thing app)
    {
        if (!MP.IsInMultiplayer || !MP.InInterface) return true;
        if (pawn?.jobs == null || app == null) return false;

        if (MP.IsExecutingSyncCommand)
            EnqueueOrder(() => SyncedWearFromInventory(pawn, app));
        else
            SyncedWearFromInventory(pawn, app);
        return false;
    }

    [MpCompatPrefix(typeof(CommandUtility), nameof(CommandUtility.CommandDrop))]
    private static bool Prefix_CommandDrop(Pawn pawn, Thing t)
    {
        if (!MP.IsInMultiplayer || !MP.InInterface) return true;
        if (pawn?.jobs == null || t == null) return false;

        if (MP.IsExecutingSyncCommand)
            EnqueueOrder(() => SyncedDrop(pawn, t));
        else
            SyncedDrop(pawn, t);
        return false;
    }

    [MpCompatPrefix(typeof(CommandUtility), nameof(CommandUtility.CommandEquipWeaponFromInventory))]
    private static bool Prefix_CommandEquipWeapon(Pawn pawn, ThingWithComps item)
    {
        if (!MP.IsInMultiplayer || !MP.InInterface) return true;
        if (pawn?.equipment == null || item == null) return false;

        if (MP.IsExecutingSyncCommand)
            EnqueueOrder(() => SyncedEquipWeapon(pawn, item));
        else
            SyncedEquipWeapon(pawn, item);
        return false;
    }

    [MpCompatPrefix(typeof(CommandUtility), nameof(CommandUtility.CommandMoveWeaponToInventory))]
    private static bool Prefix_CommandMoveWeapon(Pawn pawn, ThingWithComps item)
    {
        if (!MP.IsInMultiplayer || !MP.InInterface) return true;
        if (pawn?.equipment == null || item == null) return false;

        if (MP.IsExecutingSyncCommand)
            EnqueueOrder(() => SyncedMoveWeaponToInventory(pawn, item));
        else
            SyncedMoveWeaponToInventory(pawn, item);
        return false;
    }

    // Bulk optimize dialog: compute the wear/drop sets locally from the acting
    // client's dialog state, then issue one synced order per item so no
    // List<Thing> ever crosses the wire. Window close stays local (remotes
    // never opened the dialog).
    [MpCompatPrefix(typeof(Dialog_OptimizeEquipment), "CommandWear", new Type[0])]
    private static bool Prefix_OptimizeWear(
        Dialog_OptimizeEquipment __instance,
        Pawn ___pawn,
        List<Apparel> ___FinalApparel)
    {
        if (!MP.IsInMultiplayer || !MP.InInterface || MP.IsExecutingSyncCommand) return true;

        var pawn = ___pawn;
        var final = ___FinalApparel;
        if (pawn?.apparel == null || final == null) return false;

        var worn = pawn.apparel.WornApparel.ToHashSet();
        var finalSet = final.Where(a => a != null).ToHashSet();

        foreach (var apparel in finalSet)
            if (!worn.Contains(apparel) && apparel.Spawned &&
                pawn.CanReserveAndReach(apparel, PathEndMode.Touch, Danger.Some))
                SyncedOptimizeWear(pawn, apparel);

        foreach (var apparel in worn)
            if (!finalSet.Contains(apparel))
                SyncedOptimizeDrop(pawn, apparel);

        return false;
    }

    #endregion
}