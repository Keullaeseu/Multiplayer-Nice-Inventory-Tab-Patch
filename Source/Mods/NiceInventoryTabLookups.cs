using System.Reflection;
using HarmonyLib;
using NiceInventoryTab;
using RimWorld;
using Verse;
using Verse.AI;

namespace MultiplayerNiceInventoryTabPatch.Source.Mods;

public partial class NiceInventoryTab
{
    #region Reflection lookups (cached; local-only, never synced)

    private static FieldInfo jobsGivenThisTickField;

    private static FieldInfo JobsGivenThisTickField
    {
        get
        {
            if (jobsGivenThisTickField == null)
                jobsGivenThisTickField = AccessTools.Field(typeof(Pawn_JobTracker), "jobsGivenThisTick");
            return jobsGivenThisTickField;
        }
    }

    // Mirrors Dialog_OptimizeEquipment.DecreateJobCounter: allows several StartJob calls
    // in a single tick for bulk optimize orders. Runs inside synced workers on every client.
    private static void DecreateJobCounter(Pawn_JobTracker tracker)
    {
        try
        {
            var field = JobsGivenThisTickField;
            if (field == null || tracker == null) return;
            field.SetValue(tracker, (int)field.GetValue(tracker) - 1);
        }
        catch
        {
            // Non-fatal: without the decrement only the first bulk job is taken this tick.
        }
    }

    // OutfitForcedHandler has no pawn in its syncable signature, so resolve the owner.
    // NIT always acts on the displayed pawn, check it first; fall back to a map
    // scan for any other caller. Only runs on UI clicks (rare), never on tick.
    private static Pawn PawnOfForcedHandler(object handler)
    {
        if (handler == null) return null;
        try
        {
            var last = ITab_Pawn_Gear_Patch.lastPawn;
            if (last?.outfits?.forcedHandler == handler)
                return last;

            foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawns)
                if (pawn?.outfits?.forcedHandler == handler)
                    return pawn;
        }
        catch
        {
            // Non-fatal: caller falls back to running the original locally.
        }

        return null;
    }

    // BillFinished_Patch is internal to the NIT assembly, so Register is invoked
    // via reflection (cached). Runs inside the synced create worker on every client.
    private static FastInvokeHandler billRegisterHandler;
    private static bool billRegisterResolved;

    private static void RegisterWearRequest(Bill_ProductionWithUft bill, Pawn pawn, ApparelLayerDef layer)
    {
        if (bill == null || pawn == null || layer == null) return;

        if (!billRegisterResolved)
        {
            billRegisterResolved = true;
            var type = AccessTools.TypeByName("NiceInventoryTab.BillFinished_Patch");
            var method = type == null
                ? null
                : AccessTools.Method(type, "Register",
                    new[] { typeof(Bill_ProductionWithUft), typeof(Pawn), typeof(ApparelLayerDef) });
            if (method != null)
                billRegisterHandler = MethodInvoker.GetHandler(method);
            else
                Log.Warning($"{LogPrefix} Could not find BillFinished_Patch.Register - auto-wear stays local.");
        }

        billRegisterHandler?.Invoke(null, bill, pawn, layer);
    }

    private static FieldInfo wornApparelVersionField;
    private static FieldInfo wornEquipmentVersionField;
    private static FieldInfo inventoryVersionField;

    // NIT gates row rebuilds on these private version counters (same version means
    // widgets untouched). Resetting them makes the next Recache rebuild every row
    // through UpdateStats, picking up externally synced changes (like forcedWear)
    // that don't alter the worn lists themselves.
    private static void ResetWornVersions()
    {
        try
        {
            var type = AccessTools.TypeByName("NiceInventoryTab.ITab_Pawn_Gear_Patch");
            if (type == null) return;
            wornApparelVersionField ??= AccessTools.Field(type, "lastWornApparelVersion");
            wornEquipmentVersionField ??= AccessTools.Field(type, "lastWornEquipmentVersion");
            inventoryVersionField ??= AccessTools.Field(type, "lastInventoryVersion");
            wornApparelVersionField?.SetValue(null, -1);
            wornEquipmentVersionField?.SetValue(null, -1);
            inventoryVersionField?.SetValue(null, -1);
        }
        catch
        {
            // Non-fatal: worst case the icon refreshes on the next list change.
        }
    }

    #endregion
}