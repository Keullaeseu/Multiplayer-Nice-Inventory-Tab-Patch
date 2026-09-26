using Multiplayer.Compat;
using Verse;

namespace MultiplayerNiceInventoryTabPatch.Source.Mods;

/// <summary>
///     Multiplayer patch for Nice Inventory Tab by Andromeda, Last Update: 3 Apr @ 4:19pm 2026
///     https://steamcommunity.com/sharedfiles/filedetails/?id=3609897594
///     Follows the rwmt/Multiplayer-Compatibility pattern (see Multiplayer-Nice-Bill-Tab-Patch):
///     UI event handlers are Harmony-prefixed and redirected into [MpCompatSyncMethod]
///     workers that only touch game state, so every client executes the same mutation.
///     What is synced:
///     - crafting bills from empty apparel slots (CommandCreate, incl. auto-wear registration)
///     - wear / move-to-inventory / wear-from-inventory job orders (CommandWear, etc.)
///     - drop / unequip orders, including direct inventory TryDrop path (CommandDrop)
///     - direct weapon equip / stow container transfers (CommandEquip/MoveWeaponToInventory)
///     - forced-wear toggles (OutfitForcedHandler.SetForced via pawn lookup)
///     - ingest-from-inventory buttons (vanilla method, MP core syncs it)
///     - optimize dialog bulk wear/drop (Dialog_OptimizeEquipment, per-item synced orders)
///     MP wraps FloatMenuOption choices in a sync scope it can only replay for
///     registered delegates; NIT's custom menus use unregistered lambdas, so those
///     clicks are deferred to the next UI draw where they broadcast normally.
///     Relied upon from Multiplayer core (not patched here):
///     - BillStack.AddBill (called inside the synced create worker)
///     - Pawn_JobTracker.TryTakeOrderedJob (called inside synced workers)
///     - custom JobDrivers (NIT_MoveApparelToInventory / NIT_WearFromInventory run on tick)
///     - apparel/equipment/inventory Notify_* listeners (already set shouldRecache on every client)
///     Known limitations (documented, not silently broken):
///     - dev-mode "DEV create" apparel spawning goes through direct apparel.Wear and is left
///     untouched on purpose (same as vanilla dev spawning, use at own risk in MP).
///     - Better Workbench / third-party strip (NonUnoPinata) actions keep their own behavior;
///     NonUnoPinata is covered by its own compat when present.
///     Pure UI state (ITab_Pawn_Gear_Patch statics, selections, scroll positions, FloatRef
///     caches, camera jumps, messages, sounds) is intentionally never synced.
///     Code layout (one concern per file, all the same partial class):
///     - NiceInventoryTab.cs - entry point: mod attribute, constructor, LatePatch.
///     - NiceInventoryTabSynced.cs - [MpCompatSyncMethod] workers (run on every client).
///     - NiceInventoryTabCommandPatches.cs - NIT's own UI handlers (CommandUtility, dialog).
///     - NiceInventoryTabVanillaPatches.cs - vanilla methods NIT drives (forced, ingest, stats).
///     - NiceInventoryTabDeferred.cs - deferred-order queue, drain hooks, stack checks.
///     - NiceInventoryTabLookups.cs - cached reflection lookups (never synced).
/// </summary>
[MpCompatFor("Andromeda.NiceInventoryTab")]
public partial class NiceInventoryTab
{
    private const string LogPrefix = "[Multiplayer Nice Inventory Tab Patch]";

    public NiceInventoryTab(ModContentPack content)
    {
        LongEventHandler.ExecuteWhenFinished(LatePatch);
    }

    private void LatePatch()
    {
        Log.Message($"{LogPrefix} Initializing...");

        // Registers all [MpCompatPrefix]/[MpCompatSyncMethod] methods in this partial class.
        // NOTE: FoodUtility.IngestFromInventoryNow is deliberately NOT registered here -
        // MP core already syncs it (double registration would double-sync). NIT menu
        // clicks are routed to it through the deferred queue instead (see Prefix_Ingest).
        MpCompatPatchLoader.LoadPatch(this);

        Log.Message($"{LogPrefix} Initialized.");
    }
}