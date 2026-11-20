// C&C-specific gameplay adapter. The original mod and generic API stay unchanged.
// Uses the installed mod's cooking/rest implementation; never borrows Camping.Tent/Fire.
using System;
using System.Reflection;
using UnityEngine;
using DaggerfallCoop.API;
using DaggerfallCoop.ObjectSync;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.Utility.ModSupport;

namespace DaggerfallCoop.Compatibility
{
    public sealed class ClimatesCaloriesCampCompatibility : MonoBehaviour
    {
        const string Profile = "climates-calories-camps";
        const string ModTitle = "Climates & Calories";
        const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        static ClimatesCaloriesCampCompatibility instance;
        DaggerfallMessageBox.OnButtonClickHandler nativeMenu;
        DaggerfallListPickerWindow.OnItemPickedEventHandler nativePick;
        DaggerfallMessageBox.MessageBoxButtons restButton, cookButton, cancelButton;
        bool warned;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (instance != null) return;
            var go = new GameObject("C&C Shared Camp Compatibility");
            DontDestroyOnLoad(go);
            go.AddComponent<ClimatesCaloriesCampCompatibility>();
        }
        void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;
            Debug.Log("[CCCampMP] v0.20 loaded; original camp button rendering restored.");
            if (!GenericModObjectSync.RegisterActivationHandler(Profile, Activate))
                Debug.LogWarning("[CCCampMP] A camp interaction adapter is already registered.");
        }
        void OnDestroy()
        {
            if (instance != this) return;
            GenericModObjectSync.UnregisterActivationHandler(Profile, Activate);
            instance = null;
        }

        static bool Ready(out GameObject player, object ownedWindow)
        {
            player = null;
            if (!MultiplayerAPI.IsClient || MultiplayerAPI.LocalPlayerId == 0 || !GameManager.HasInstance ||
                SaveLoadManager.Instance == null || SaveLoadManager.Instance.LoadInProgress) return false;
            // IsPlayingGame returns false while our own popup/picker pauses gameplay too.
            // Only that exact window can relax the HUD check, never a load/main/pause menu.
            if (!GameManager.Instance.IsPlayingGame() && (ownedWindow == null ||
                !ReferenceEquals(DaggerfallUI.UIManager.TopWindow, ownedWindow))) return false;
            player = GameObject.FindGameObjectWithTag("Player");
            return player != null && player.activeInHierarchy;
        }
        static bool CanUseCamp(SyncedModObject camp, Collider collider, out string reason, object ownedWindow = null)
        {
            reason = "This camp is no longer available.";
            GameObject player;
            if (!Ready(out player, ownedWindow) || camp == null || !camp.Replica || !camp.gameObject.activeInHierarchy ||
                camp.ProfileId != Profile || (camp.RuleId != "tent" && camp.RuleId != "fire") ||
                collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy ||
                collider.GetComponent<SyncedModObject>() != camp) return false;
            var mod = ModManager.Instance.GetMod(ModTitle);
            if (mod == null || !mod.Enabled || !mod.IsReady) return false;
            var binding = camp.GetComponentInParent<ModWorldBinding>();
            var record = binding == null ? null : binding.Record;
            Vector3 position; Quaternion rotation;
            if (record == null || record.Pose == null || record.Key != camp.Key || record.Kind != camp.RuleId ||
                record.OwnerPlayerId != camp.OwnerPlayerId || !record.Pose.TryLocal(out position, out rotation)) return false;
            // Reject a stale visual between a travel/origin change and its next pose update.
            if ((position - binding.transform.position).sqrMagnitude > 1f) return false;
            if (camp.OwnerPlayerId == 0 || camp.OwnerPlayerId == MultiplayerAPI.LocalPlayerId ||
                !MultiplayerAPI.AreInSameParty(MultiplayerAPI.LocalPlayerId, camp.OwnerPlayerId))
            { reason = "You must be in the camp owner's party to use this camp."; return false; }
            if (Vector3.Distance(player.transform.position, collider.ClosestPoint(player.transform.position)) >
                PlayerActivate.DefaultActivationDistance)
            { reason = "You are too far from this camp."; return false; }
            if (GameManager.Instance.AreEnemiesNearby(true))
            { reason = "There are enemies nearby."; return false; }
            return true;
        }
        bool ResolveCampActions()
        {
            if (nativeMenu != null && nativePick != null) return true;
            try
            {
                Type camping = null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    camping = assembly.GetType("ClimatesCalories.Camping", false);
                    if (camping != null) break;
                }
                if (camping == null) throw new MissingMemberException("ClimatesCalories.Camping");
                var menu = camping.GetMethod("CampPopUp_OnButtonClick", StaticMembers, null,
                    new[] { typeof(DaggerfallMessageBox), typeof(DaggerfallMessageBox.MessageBoxButtons) }, null);
                var pick = camping.GetMethod("Cook_OnItemPicked", StaticMembers, null,
                    new[] { typeof(int), typeof(string) }, null);
                var cook = camping.GetField("cookButton", StaticMembers);
                var rest = camping.GetField("restButton", StaticMembers);
                var cancel = camping.GetField("cancelButton", StaticMembers);
                if (menu == null || pick == null || cook == null || rest == null || cancel == null ||
                    cook.FieldType != typeof(DaggerfallMessageBox.MessageBoxButtons) ||
                    rest.FieldType != typeof(DaggerfallMessageBox.MessageBoxButtons) ||
                    cancel.FieldType != typeof(DaggerfallMessageBox.MessageBoxButtons))
                    throw new MissingMemberException("C&C camp callbacks/buttons have changed");
                cookButton = (DaggerfallMessageBox.MessageBoxButtons)cook.GetValue(null);
                restButton = (DaggerfallMessageBox.MessageBoxButtons)rest.GetValue(null);
                cancelButton = (DaggerfallMessageBox.MessageBoxButtons)cancel.GetValue(null);
                nativeMenu = (DaggerfallMessageBox.OnButtonClickHandler)Delegate.CreateDelegate(
                    typeof(DaggerfallMessageBox.OnButtonClickHandler), menu);
                nativePick = (DaggerfallListPickerWindow.OnItemPickedEventHandler)Delegate.CreateDelegate(
                    typeof(DaggerfallListPickerWindow.OnItemPickedEventHandler), pick);
                Debug.Log("[CCCampMP] Native C&C camp callbacks ready; guests have Rest/Cook/Cancel. Packing is owner-only.");
                return true;
            }
            catch (Exception error)
            {
                nativeMenu = null; nativePick = null;
                if (!warned) { warned = true; Debug.LogWarning("[CCCampMP] Camp actions unavailable: " + error.Message); }
                return false;
            }
        }
        bool Activate(SyncedModObject camp, RaycastHit hit)
        {
            if (!isActiveAndEnabled) return false;
            string reason;
            if (!CanUseCamp(camp, hit.collider, out reason)) { DaggerfallUI.MessageBox(reason); return true; }
            if (!ResolveCampActions())
            { DaggerfallUI.MessageBox("Shared camp actions are unavailable with this version of Climates & Calories."); return true; }
            var popup = new DaggerfallMessageBox(DaggerfallUI.UIManager, DaggerfallUI.UIManager.TopWindow);
            popup.SetText(new[] { "Do you wish to rest or cook?", "Only the camp's owner can pack it." });
            popup.AddButton(restButton);
            popup.AddButton(cookButton);
            popup.AddButton(cancelButton, true);
            popup.OnButtonClick += (sender, button) =>
            {
                // Only Rest and Cook can reach the native dispatcher. Never forward Pack.
                if (button != cookButton && button != restButton) { nativeMenu(sender, cancelButton); return; }
                string changed;
                if (!CanUseCamp(camp, hit.collider, out changed, sender))
                { nativeMenu(sender, cancelButton); DaggerfallUI.MessageBox(changed); return; }
                if (button == restButton)
                {
                    // Native Rest sets C&C's local camping state and opens its normal
                    // rest window. It does not require or replace Camping.Tent/Fire.
                    nativeMenu(sender, restButton);
                    return;
                }
                nativeMenu(sender, cookButton);
                var picker = DaggerfallUI.UIManager.TopWindow as DaggerfallListPickerWindow;
                if (picker == null) return; // Native "nothing to cook" response needs no interception.
                // Keep native inventory, skillet, food and time logic. Validate again at selection,
                // since the owner can pack, or the player can leave the party while a UI is open.
                picker.OnItemPicked -= nativePick;
                picker.OnItemPicked += (index, name) =>
                {
                    string unavailable;
                    if (!CanUseCamp(camp, hit.collider, out unavailable, picker))
                    {
                        picker.CloseWindow(); nativeMenu(sender, cancelButton);
                        DaggerfallUI.MessageBox(unavailable); return;
                    }
                    nativePick(index, name);
                    Debug.Log("[CCCampMP] Native cooking selection completed at party camp owner=" + camp.OwnerPlayerId + ".");
                };
            };
            popup.Show();
            return true;
        }
    }
}
