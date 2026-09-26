using System.Runtime.CompilerServices;
using Multiplayer.Compat;
using NiceInventoryTab;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace MultiplayerNiceInventoryTabPatch.Source.Mods;

public partial class NiceInventoryTab
{
    #region Synced workers (game state only, run on every client)

    // NOTE: all workers intentionally use the default (None) sync context.
    // SyncContext.MapSelected caused intermittent one-sided execution (proven by
    // host-vs-client JIT comparison: clicker starts JobDriver_Wear, remote never
    // does): when the executor isn't viewing that map (world map, etc.) the order
    // is dropped there while the clicker runs it. Pawn/Thing args already carry
    // their maps, and every body null-checks, so no gating is needed.

    // Exact game-state half of CommandUtility.CommandCreate. The target pawn travels
    // explicitly because the original reads the UI static ITab_Pawn_Gear_Patch.lastPawn,
    // which differs per client. Bill + wear request are built inside the sync so every
    // client registers its own local Bill instance deterministically.
    [MpCompatSyncMethod]
    private static void SyncedCommandCreate(
        Building_WorkTable wtable,
        ThingDef appdef,
        RecipeDef recipe,
        ApparelLayerDef apparelLayer,
        Pawn targetPawn)
    {
        if (wtable?.billStack == null || recipe == null || appdef == null || targetPawn == null || apparelLayer == null)
            return;

        var bill = recipe.MakeNewBill();
        if (bill is Bill_ProductionWithUft uftBill)
            RegisterWearRequest(uftBill, targetPawn, apparelLayer);
        if (Settings.AutoRenameBillLabel && bill is Bill_Production productionBill)
            productionBill.RenamableLabel = "NIT_AutoNameRecipe".Translate(appdef.LabelCap, targetPawn.LabelShortCap);
        wtable.billStack.AddBill(bill);

        ITab_Pawn_Gear_Patch.shouldRecache = true;
    }

    // Game-state half of CommandUtility.CommandWear (job order only, no camera).
    // NOTE: the original uses StartJob(InterruptForced), but the synced worker issues
    // the order through TryTakeOrderedJob like the vanilla float-menu Wear order.
    // StartJob force-ends the current job and re-queues it, which trips MP's
    // JobTrackerStart/EndCurrentJob handling and third-party transition lockouts
    // (e.g. BlackScience) asymmetrically and desyncs ("Wrong random state", pawn
    // walking on one client only). TryTakeOrderedJob is MP's blessed job-order
    // primitive (own prefix + sync template) and behaves like vanilla Wear.
    // Behavior difference vs single-player: the order queues instead of interrupting.
    [MpCompatSyncMethod]
    private static void SyncedCommandWear(Pawn pawn, Thing app, ThingDef appDef, IntVec3 appPos)
    {
        if (pawn?.jobs == null) return;

        // A runtime-spawned item's ID can fail to resolve on a client (proven by
        // host-side SKIPPED with appNull=True) while the world is otherwise in sync.
        // Fall back to stable attributes (def + position); both sides then converge
        // on the same pick - or both skip when it is genuinely gone.
        var viaFallback = false;
        if (app == null)
        {
            app = FindLooseApparel(pawn, appDef, appPos);
            viaFallback = true;
        }

        if (app == null)
        {
            Log.Warning($"{LogPrefix} Wear order dropped: item no longer resolves (def={appDef?.defName})");
            return;
        }

        // TEMP DIAGNOSTIC - will be removed once the intermittent Wear desync is root-caused.
        var job = JobMaker.MakeJob(JobDefOf.Wear, app);
        job.playerForced = true;
        var accepted = pawn.jobs.TryTakeOrderedJob(job);
        Log.Message(
            $"{LogPrefix} DIAG Wear executing: pawn={pawn.LabelShort} app={app.def?.defName} fallback={viaFallback} accepted={accepted} curJob={pawn.jobs.curJob?.def?.defName ?? "none"}");
    }

    // Deterministic re-resolution of a wear target by stable attributes only (never
    // by thingID): nearest matching spawned apparel to the click-time position, with
    // HP/stack/quality tie-breaks. All inputs are sim-synced, so every client that
    // falls back picks the same item - or all skip together.
    private static Thing FindLooseApparel(Pawn pawn, ThingDef def, IntVec3 near)
    {
        var map = pawn?.Map;
        if (map == null || def == null) return null;

        Thing best = null;
        var bestDist = float.MaxValue;
        var bestHp = -1;
        var bestStack = -1;
        var bestQuality = -1;

        foreach (var thing in map.listerThings.ThingsOfDef(def))
        {
            if (!(thing is Apparel) || !thing.Spawned || thing.IsForbidden(pawn)) continue;
            thing.TryGetQuality(out var qc);
            var dist = (float)(thing.Position - near).LengthHorizontalSquared;
            if (best == null || dist < bestDist ||
                (Math.Abs(dist - bestDist) < 0.001f &&
                 (thing.HitPoints > bestHp ||
                  (thing.HitPoints == bestHp && (thing.stackCount > bestStack ||
                                                 (thing.stackCount == bestStack && (int)qc > bestQuality))))))
            {
                best = thing;
                bestDist = dist;
                bestHp = thing.HitPoints;
                bestStack = thing.stackCount;
                bestQuality = (int)qc;
            }
        }

        return best;
    }

    // Game-state half of CommandUtility.CommandRemoveToInventory.
    [MpCompatSyncMethod]
    private static void SyncedRemoveToInventory(Pawn pawn, Thing app, bool start)
    {
        if (pawn?.jobs == null || app == null) return;

        var job = JobMaker.MakeJob(Assets.NIT_MoveApparelToInventory, app);
        job.playerForced = true;
        if (start)
            pawn.jobs.StopAll();
        pawn.jobs.TryTakeOrderedJob(job, requestQueueing: true);
    }

    // Game-state half of CommandUtility.CommandWearFromInventory. Conflicting apparel
    // is queued inline (same as the original) instead of nesting another synced call,
    // so the whole order broadcasts exactly once.
    [MpCompatSyncMethod]
    private static void SyncedWearFromInventory(Pawn pawn, Thing app)
    {
        if (pawn?.jobs == null || app == null || pawn.apparel == null) return;

        pawn.jobs.StopAll();
        var wornApparel = pawn.apparel.WornApparel;
        for (var index = wornApparel.Count - 1; index >= 0; --index)
            if (!ApparelUtility.CanWearTogether(app.def, wornApparel[index].def, pawn.RaceProps.body))
            {
                var removeJob = JobMaker.MakeJob(Assets.NIT_MoveApparelToInventory, wornApparel[index]);
                removeJob.playerForced = true;
                pawn.jobs.TryTakeOrderedJob(removeJob, requestQueueing: true);
            }

        var job = JobMaker.MakeJob(Assets.NIT_WearFromInventory, app);
        job.playerForced = true;
        pawn.jobs.TryTakeOrderedJob(job, requestQueueing: true);
    }

    // Game-state half of CommandUtility.CommandDrop, including the direct
    // inventory TryDrop path (no job involved there).
    [MpCompatSyncMethod]
    private static void SyncedDrop(Pawn pawn, Thing t)
    {
        if (pawn?.jobs == null || t == null) return;

        if (t is Apparel apparel && pawn.apparel != null && pawn.apparel.WornApparel.Contains(apparel))
        {
            pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.RemoveApparel, apparel));
        }
        else if (t is ThingWithComps equipment && pawn.equipment != null &&
                 pawn.equipment.AllEquipmentListForReading.Contains(equipment))
        {
            pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.DropEquipment, equipment));
        }
        else
        {
            if (t.def.destroyOnDrop) return;
            pawn.inventory.innerContainer.TryDrop(t, pawn.Position, pawn.Map, ThingPlaceMode.Near,
                out _);
        }

        ITab_Pawn_Gear_Patch.shouldRecache = true;
    }

    // Game-state half of CommandUtility.CommandEquipWeaponFromInventory. Immediate
    // container transfer + AddEquipment (no job), so it must run identically everywhere.
    [MpCompatSyncMethod]
    private static void SyncedEquipWeapon(Pawn pawn, ThingWithComps item)
    {
        if (pawn?.equipment == null || item == null || pawn.inventory?.innerContainer == null) return;

        var container = pawn.inventory.innerContainer;
        pawn.jobs?.StopAll();

        if (pawn.equipment.Primary != null)
        {
            if (CommandUtility.CanFitInInventory(pawn, pawn.equipment.Primary.def, out _, true))
                pawn.equipment.TryTransferEquipmentToContainer(pawn.equipment.Primary, container);
            else
                pawn.equipment.MakeRoomFor(item);
        }

        if (container.Contains(item) && container.Take(item, 1) is ThingWithComps taken)
        {
            pawn.equipment.AddEquipment(taken);
            item.def.soundInteract?.PlayOneShot(new TargetInfo(pawn.Position, pawn.MapHeld));
        }

        ITab_Pawn_Gear_Patch.shouldRecache = true;
        FloatRef.ClearValues();
    }

    // Game-state half of CommandUtility.CommandMoveWeaponToInventory.
    [MpCompatSyncMethod]
    private static void SyncedMoveWeaponToInventory(Pawn pawn, ThingWithComps item)
    {
        if (pawn?.equipment?.Primary == null || item == null || pawn.inventory?.innerContainer == null) return;

        var container = pawn.inventory.innerContainer;
        if (CommandUtility.CanFitInInventory(pawn, pawn.equipment.Primary.def, out _, true))
            pawn.equipment.TryTransferEquipmentToContainer(pawn.equipment.Primary, container);
        else
            pawn.equipment.MakeRoomFor(item);

        ITab_Pawn_Gear_Patch.shouldRecache = true;
        FloatRef.ClearValues();
    }

    // Synced forced-wear toggle. Takes the pawn explicitly because the vanilla
    // handler instance itself has no MP sync worker. Uses default (None) context
    // on purpose: MapSelected rejects the broadcast in some tab states even though
    // the pawn arg carries its map, which silently leaves Force/Unforce local-only.
    // NoInlining is load-bearing: Prefix_SetForced detects this frame on the stack
    // to tell our own execution apart from MP-wrapped menu clicks.
    [MpCompatSyncMethod]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SyncedSetForced(Pawn pawn, Apparel apparel, bool forced)
    {
        if (pawn?.outfits?.forcedHandler == null || apparel == null) return;
        pawn.outfits.forcedHandler.SetForced(apparel, forced);

        // NIT only rebuilds item rows when worn-list versions change; forcing changes
        // no list, so remote widgets would keep their stale forcedWear flag forever
        // (the clicker sees the icon only because NIT's menu lambda flips its own
        // widget). Resetting the versions forces a full rebuild with fresh IsForced
        // reads on every client. Same cost as wearing an item, click-rare.
        ResetWornVersions();
        ITab_Pawn_Gear_Patch.shouldRecache = true;
    }

    // Single optimize-dialog wear order (bulk loop lives in the prefix).
    [MpCompatSyncMethod]
    private static void SyncedOptimizeWear(Pawn pawn, Thing app)
    {
        if (pawn?.jobs == null || app == null) return;
        var job = JobMaker.MakeJob(JobDefOf.Wear, app);
        job.playerForced = true;
        pawn.jobs.StartJob(job, JobCondition.InterruptForced, resumeCurJobAfterwards: true);
        DecreateJobCounter(pawn.jobs);
    }

    // Single optimize-dialog drop order (bulk loop lives in the prefix).
    [MpCompatSyncMethod]
    private static void SyncedOptimizeDrop(Pawn pawn, Thing app)
    {
        if (pawn?.jobs == null || app == null) return;
        var job = JobMaker.MakeJob(JobDefOf.RemoveApparel, app);
        job.playerForced = true;
        pawn.jobs.StartJob(job, JobCondition.InterruptForced, resumeCurJobAfterwards: true);
        DecreateJobCounter(pawn.jobs);
    }

    #endregion
}