// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2023 Daggerfall Workshop
// Web Site:        http://www.dfworkshop.net
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Source Code:     https://github.com/Interkarma/daggerfall-unity
// Original Author: Gavin Clayton (interkarma@dfworkshop.net)
// Contributors:    
// 
// Notes:
//

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using FullSerializer;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Utility;
using DaggerfallWorkshop.Game.Formulas;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.Utility;

namespace DaggerfallWorkshop.Game.MagicAndEffects.MagicEffects
{
    /// <summary>
    /// Stage one disease effect for vampirism.
    /// Handles deployment tasks over three-day infection window.
    /// This disease can be cured in the usual way up until it completes.
    /// Note: This disease should only be assigned to player entity.
    ///
    /// TODO:
    ///  * Clear guild memberships and reset reputations
    /// </summary>
    public class VampirismInfection : DiseaseEffect
    {
        public const string VampirismInfectionKey = "Vampirism-Infection";
        const string spellsFilename = "SPELLS.STD";

        uint startingDay = 0;
        bool warningDreamVideoScheduled = false;
        bool warningDreamVideoPlayed = false;
        bool fakeDeathVideoPlayed = false;
        int infectionRegionIndex = -1;

        public override void SetProperties()
        {
            properties.Key = VampirismInfectionKey;
            properties.ShowSpellIcon = false;
            classicDiseaseType = Diseases.None;
            diseaseData = new DiseaseData(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF); // Permanent no-effect disease, will manage custom lifecycle
            bypassSavingThrows = true;
        }

        public override TextFile.Token[] ContractedMessageTokens => null;

        public int InfectionRegionIndex
        {
            get { return infectionRegionIndex; }
        }

        public VampireClans InfectionVampireClan
        {
            get { return FormulaHelper.GetVampireClan(infectionRegionIndex); }
        }

        protected override void BecomeIncumbent()
        {
            base.BecomeIncumbent();

            // If player already has a racial override in place (e.g. vampire/lycanthrope) then just cancel infection process
            if (manager.GetRacialOverrideEffect() != null)
                EndDisease();
        }

        protected override void AddState(IncumbentEffect incumbent)
        {
            // While there can only be a single disease incumbent per key, incoming effect can remain memory resident for a short time
            // This can present duplicate symptoms during time acceleration (e.g. fast travel) from instances waiting to expire
            // Explicitly terminate non-incumbent payload that it doesn't fire during time acceleration
            EndDisease();
        }

        public override void Start(EntityEffectManager manager, DaggerfallEntityBehaviour caster = null)
        {
            base.Start(manager, caster);

            // Record starting day of infection
            startingDay = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToClassicDaggerfallTime() / DaggerfallDateTime.MinutesPerDay;

            // Record region of infection for clan at time of deployment
            // Think classic uses current region at time of turning, this will use current region at time of infection
            infectionRegionIndex = GameManager.Instance.PlayerGPS.CurrentRegionIndex;
        }

        public override void Resume(EntityEffectManager.EffectSaveData_v1 effectData, EntityEffectManager manager, DaggerfallEntityBehaviour caster = null)
        {
            base.Resume(effectData, manager, caster);
        }

        protected override void UpdateDisease()
        {
            // Not calling base as this is a very custom disease that manages its own lifecycle
            ProgressDisease();
        }

        #region Private Methods

        void ProgressDisease()
        {
            const string dreamVideoName = "ANIM0004.VID";   // Vampire dream video
            const string deathVideoName = "ANIM0012.VID";   // Death video

            // Do nothing if not incumbent or effect ended
            if (!IsIncumbent || forcedRoundsRemaining == 0 || daysOfSymptomsLeft == completedDiseaseValue)
                return;

            // Get current day and number of days that have passed (e.g. fast travel can progress time several days)
            uint currentDay = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToClassicDaggerfallTime() / DaggerfallDateTime.MinutesPerDay;
            int daysPast = (int)(currentDay - startingDay);

            // Show dream after 1 day has passed, progress to full-blown vampirism after 3 days have passed
            if (daysPast > 0 && !warningDreamVideoScheduled && !warningDreamVideoPlayed)
            {
                // Play infection warning dream video
                DaggerfallVidPlayerWindow vidPlayerWindow = (DaggerfallVidPlayerWindow)
                    UIWindowFactory.GetInstanceWithArgs(UIWindowType.VidPlayer, new object[] { DaggerfallUI.UIManager, dreamVideoName });
                vidPlayerWindow.EndOnAnyKey = false;
                DaggerfallUI.UIManager.PushWindow(vidPlayerWindow);
                vidPlayerWindow.OnClose += WarningDreamVideoCompleted;
                warningDreamVideoScheduled = true;
            }
            else if (daysPast > 3 && warningDreamVideoPlayed && !fakeDeathVideoPlayed)
            {
                // Play "death" video ahead of final stage of infection
                DaggerfallVidPlayerWindow vidPlayerWindow = (DaggerfallVidPlayerWindow)
                    UIWindowFactory.GetInstanceWithArgs(UIWindowType.VidPlayer, new object[] { DaggerfallUI.UIManager, deathVideoName });
                vidPlayerWindow.EndOnAnyKey = false;
                DaggerfallUI.UIManager.PushWindow(vidPlayerWindow);
                vidPlayerWindow.OnClose += DeployFullBlownVampirism;
                fakeDeathVideoPlayed = true;
            }
        }

        private void WarningDreamVideoCompleted()
        {
            warningDreamVideoPlayed = true;
        }

        private void DeployFullBlownVampirism()
        {
            PlayerEnterExit playerEnterExit = GameManager.Instance != null ? GameManager.Instance.PlayerEnterExit : null;
            if (playerEnterExit == null)
            {
                Debug.LogError("[VampireTransform] Could not find PlayerEnterExit. Running original immediate vampire transform fallback.");
                DeployFullBlownVampirismImmediateFallback();
                return;
            }

            // Important: do not apply the permanent vampire curse until the cemetery
            // transition has completed. During fast travel, the two-week RaiseTime() below
            // can make EntityEffectBroker process synthetic catch-up rounds immediately.
            // If the player gains SunDamage/HolyDamage before PlayerEnterExit has actually
            // finished moving into the crypt, the stale exterior/interior context can kill
            // the new vampire before the fake-death wake-up completes.
            playerEnterExit.StartCoroutine(DeployFullBlownVampirismCoroutine(playerEnterExit));
        }

        private void DeployFullBlownVampirismImmediateFallback()
        {
            const int deathIsNotEternalTextID = 401;

            if (DaggerfallUI.Instance.UserInterfaceManager.TopWindow is DaggerfallRestWindow)
                (DaggerfallUI.Instance.UserInterfaceManager.TopWindow as DaggerfallRestWindow).CloseWindow();

            GameManager.Instance.PlayerEntity.PreventEnemySpawns = true;

            if (IsPureClientWithHostAuthoritativeTime())
            {
                Debug.Log("[VampireMP] Immediate fallback on pure client under host-authoritative time: skipping local two-week vampire RaiseTime.");
                if (GameManager.Instance != null && GameManager.Instance.EntityEffectBroker != null)
                    GameManager.Instance.EntityEffectBroker.AlignMagicRoundTimerToCurrentTime("VampireMP-immediate-fallback-skip-client-two-week-timejump");
            }
            else
            {
                float raiseTime = (2 * DaggerfallDateTime.SecondsPerWeek) + (DaggerfallDateTime.DuskHour + 1 - DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.Hour) * 3600;
                DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.RaiseTime(raiseTime);
                GameManager.Instance.EntityEffectBroker.SyntheticTimeIncrease = true;
            }

            DFLocation location = GetRandomCemetery();
            DFPosition mapPixel = MapsFile.LongitudeLatitudeToMapPixel(location.MapTableData.Longitude, location.MapTableData.Latitude);
            DFPosition worldPos = MapsFile.MapPixelToWorldCoord(mapPixel.X, mapPixel.Y);
            GameManager.Instance.PlayerEnterExit.RespawnPlayer(worldPos.X, worldPos.Y, true, false);

            CompleteFullBlownVampirism(deathIsNotEternalTextID, "immediate-fallback");
        }

        private IEnumerator DeployFullBlownVampirismCoroutine(PlayerEnterExit playerEnterExit)
        {
            const int deathIsNotEternalTextID = 401;

            // Cancel rest window if sleeping
            if (DaggerfallUI.Instance.UserInterfaceManager.TopWindow is DaggerfallRestWindow)
                (DaggerfallUI.Instance.UserInterfaceManager.TopWindow as DaggerfallRestWindow).CloseWindow();

            // Halt random enemy spawns for next playerEntity update so player isn't bombarded by spawned enemies after transform time
            GameManager.Instance.PlayerEntity.PreventEnemySpawns = true;

            bool multiplayerActive = IsMultiplayerActive();
            bool skipLocalTwoWeekTimeJump = IsPureClientWithHostAuthoritativeTime();

            // Original SP/host behaviour raises game time to an evening two weeks later.
            // Pure clients under host-authoritative MP time must NOT do this locally:
            // TimeCatcher will snap the client back to host time, leaving EntityEffectBroker
            // with a future magic-round baseline. That freezes temporary spell expiration
            // and vampire sun/holy damage until the host eventually advances past the discarded
            // two-week client jump.
            if (skipLocalTwoWeekTimeJump)
            {
                Debug.Log("[VampireMP] Pure client under host-authoritative time: skipping local two-week vampire RaiseTime. Host time remains authoritative.");
                if (GameManager.Instance != null && GameManager.Instance.EntityEffectBroker != null)
                    GameManager.Instance.EntityEffectBroker.AlignMagicRoundTimerToCurrentTime("VampireMP-skip-client-two-week-timejump");
            }
            else
            {
                // Raise game time to an evening two weeks later.
                // The permanent vampire curse is intentionally assigned only after the cemetery
                // transition is finished, so synthetic catch-up rounds cannot apply sun/holy
                // damage using the player's stale pre-transition context.
                float raiseTime = (2 * DaggerfallDateTime.SecondsPerWeek) + (DaggerfallDateTime.DuskHour + 1 - DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.Hour) * 3600;
                DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.RaiseTime(raiseTime);
                GameManager.Instance.EntityEffectBroker.SyntheticTimeIncrease = true;
            }

            DFLocation location = GetRandomCemetery();
            DFPosition mapPixel = MapsFile.LongitudeLatitudeToMapPixel(location.MapTableData.Longitude, location.MapTableData.Latitude);
            DFPosition worldPos = MapsFile.MapPixelToWorldCoord(mapPixel.X, mapPixel.Y);
            if (multiplayerActive)
            {
                StartMultiplayerVampireCemeteryWake(location, worldPos);
            }
            else
            {
                // True SP keeps the original cemetery wake-up entry call.
                GameManager.Instance.PlayerEnterExit.RespawnPlayer(
                    worldPos.X,
                    worldPos.Y,
                    true,
                    false);
            }

            yield return WaitForVampireCemeteryWakeComplete(playerEnterExit, location, multiplayerActive ? "MP" : "SP");

            // Let PlayerEnterExit.Update() refresh derived flags such as IsPlayerInSunlight
            // after IsPlayerInside/IsPlayerInsideDungeon have settled.
            yield return null;
            yield return new WaitForEndOfFrame();

            CompleteFullBlownVampirism(deathIsNotEternalTextID, multiplayerActive ? "MP-after-cemetery-wake" : "SP-after-cemetery-wake");
        }

        private IEnumerator WaitForVampireCemeteryWakeComplete(PlayerEnterExit playerEnterExit, DFLocation location, string mode)
        {
            const float timeoutSeconds = 120f;
            float timeout = Time.realtimeSinceStartup + timeoutSeconds;

            while (Time.realtimeSinceStartup < timeout)
            {
                if (IsVampireCemeteryWakeComplete(playerEnterExit, location))
                    yield break;

                yield return null;
            }

            Debug.LogWarning($"[VampireTransform] Timed out waiting for cemetery wake transition before applying vampirism. mode={mode} dungeon='{location.RegionName}/{location.Name}' inside={(playerEnterExit != null ? playerEnterExit.IsPlayerInside.ToString() : "null")} dungeonInside={(playerEnterExit != null ? playerEnterExit.IsPlayerInsideDungeon.ToString() : "null")} respawning={(playerEnterExit != null ? playerEnterExit.IsRespawning.ToString() : "null")}");
        }

        private bool IsVampireCemeteryWakeComplete(PlayerEnterExit playerEnterExit, DFLocation location)
        {
            if (playerEnterExit == null)
                return false;

            if (playerEnterExit.IsRespawning)
                return false;

            if (!playerEnterExit.IsPlayerInsideDungeon || playerEnterExit.Dungeon == null)
                return false;

            try
            {
                if (!string.Equals(playerEnterExit.Dungeon.Summary.RegionName, location.RegionName, System.StringComparison.OrdinalIgnoreCase))
                    return false;

                if (!string.Equals(playerEnterExit.Dungeon.Summary.LocationName, location.Name, System.StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            catch
            {
                return false;
            }

            return true;
        }

        private void CompleteFullBlownVampirism(int deathIsNotEternalTextID, string reason)
        {
            if (Debug.isDebugBuild)
            {
                PlayerEnterExit playerEnterExit = GameManager.Instance != null ? GameManager.Instance.PlayerEnterExit : null;
                PlayerEntity playerEntity = GameManager.Instance != null ? GameManager.Instance.PlayerEntity : null;
                Debug.Log($"[VampireTransform] Applying final vampire curse. reason={reason} hp={(playerEntity != null ? playerEntity.CurrentHealth.ToString() : "null")}/{(playerEntity != null ? playerEntity.MaxHealth.ToString() : "null")} inside={(playerEnterExit != null ? playerEnterExit.IsPlayerInside.ToString() : "null")} dungeon={(playerEnterExit != null ? playerEnterExit.IsPlayerInsideDungeon.ToString() : "null")} sunlight={(playerEnterExit != null ? playerEnterExit.IsPlayerInSunlight.ToString() : "null")} holy={(playerEnterExit != null ? playerEnterExit.IsPlayerInHolyPlace.ToString() : "null")}");
            }

            // Assign vampire spells to spellbook
            GameManager.Instance.PlayerEntity.AssignPlayerVampireSpells(InfectionVampireClan);

            // Fade in from black
            DaggerfallUI.Instance.FadeBehaviour.FadeHUDFromBlack(1.0f);

            // Start permanent vampirism effect stage two
            EntityEffectBundle bundle = GameManager.Instance.PlayerEffectManager.CreateVampirismCurse();
            GameManager.Instance.PlayerEffectManager.AssignBundle(bundle, AssignBundleFlags.BypassSavingThrows);

            // Display popup
            DaggerfallMessageBox mb = DaggerfallUI.MessageBox(deathIsNotEternalTextID);
            mb.Show();

            // Terminate custom disease lifecycle
            EndDisease();
        }

        private bool IsMultiplayerActive()
        {
            // Keep true SP on the original vanilla vampire path.
            // Do not use NetworkManager.singleton.isNetworkActive here: it can remain true/stale
            // in test scenes or after previous MP sessions and incorrectly steal the SP path.
            return NetworkServer.active || NetworkClient.active;
        }

        private bool IsPureClientWithHostAuthoritativeTime()
        {
            return NetworkClient.active && !NetworkServer.active && OptionsMultiplayer.timeHost;
        }

        private void StartMultiplayerVampireCemeteryWake(DFLocation location, DFPosition worldPos)
        {
            PlayerEnterExit playerEnterExit = GameManager.Instance != null ? GameManager.Instance.PlayerEnterExit : null;
            if (playerEnterExit == null)
            {
                Debug.LogError("[VampireMP] Could not find PlayerEnterExit. Falling back to vanilla vampire RespawnPlayer.");
                GameManager.Instance.PlayerEnterExit.RespawnPlayer(worldPos.X, worldPos.Y, true, false);
                return;
            }

            // Prefer the exact exterior dungeon entrance anchor when it can be calculated,
            // matching the TeleportPc MP path. If the cemetery has no probeable exterior
            // entrance, keep the coarse map-pixel world coordinate as a safe fallback.
            int exactEntranceWorldX;
            int exactEntranceWorldZ;
            Vector3 exactEntranceLocal;
            if (DaggerfallWorkshop.StreamingWorld.TryGetDungeonEntranceWorldCoordinates(location, out exactEntranceWorldX, out exactEntranceWorldZ, out exactEntranceLocal))
            {
                Debug.Log($"[VampireMP][ExactEntrance] Replacing coarse cemetery wake world={worldPos.X}/{worldPos.Y} with exact entrance world={exactEntranceWorldX}/{exactEntranceWorldZ} localEntrance={exactEntranceLocal}");
                worldPos = new DFPosition(exactEntranceWorldX, exactEntranceWorldZ);
            }
            else
            {
                Debug.LogWarning($"[VampireMP][ExactEntrance] Could not calculate exact cemetery entrance for '{location.RegionName}/{location.Name}'. Falling back to coarse world={worldPos.X}/{worldPos.Y}.");
            }

            // This sentinel is consumed by PlayerEnterExit.TryApplyPendingTeleportPcDungeonMarker().
            // It tells the TeleportPc-style MP enter path to avoid a quest-marker snap and
            // wait for the live network dungeon EnterMarker. Vampire wake-up should not fall back
            // to the normal dungeon StartMarker.
            Vector3 vampireWakeMarkerSentinel = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);

            bool pureClient = NetworkClient.active && !NetworkServer.active;
            if (pureClient)
            {
                playerEnterExit.RegisterMultiplayerQuestDungeonTeleportMarker(
                    location,
                    vampireWakeMarkerSentinel,
                    "VampireMP-client-before-host-request");

                playerEnterExit.PrepareMultiplayerQuestDungeonTeleportWorldPosition(
                    worldPos.X,
                    worldPos.Y,
                    "VampireMP-client-before-host-request");

                ForceSendLocalCoordinates("vampiremp-client-before-host-request");

                PlayerMultiplayer localPlayer = PlayerMultiplayer.GetLocalPlayer();
                if (localPlayer == null)
                {
                    Debug.LogError("[VampireMP] Pure client could not find local PlayerMultiplayer for vampire cemetery dungeon request.");
                    return;
                }

                Debug.Log($"[VampireMP] Pure client requesting host cemetery dungeon '{location.RegionName}/{location.Name}' world={worldPos.X}/{worldPos.Y}");
                localPlayer.RequestTeleportPcDungeonFromHost(location, vampireWakeMarkerSentinel, worldPos.X, worldPos.Y, "VampireMP-client");
                playerEnterExit.StartCoroutine(WaitForMultiplayerVampireCemeteryWake(playerEnterExit, location, "VampireMP-client-final-enter-marker"));
                return;
            }

            // Host/server path: enter immediately through the same network dungeon transition
            // used by TeleportPc. Do not use RespawnPlayer() / StartDungeonInterior().
            playerEnterExit.PrepareMultiplayerQuestDungeonTeleportWorldPosition(
                worldPos.X,
                worldPos.Y,
                "VampireMP-host-before-dungeon-request");

            playerEnterExit.RegisterMultiplayerQuestDungeonTeleportMarker(
                location,
                vampireWakeMarkerSentinel,
                "VampireMP-host-before-dungeon-request");

            ForceSendLocalCoordinates("vampiremp-host-before-dungeon-request");

            Debug.Log($"[VampireMP] Host/server requesting/entering cemetery network dungeon '{location.RegionName}/{location.Name}' world={worldPos.X}/{worldPos.Y}");
            playerEnterExit.TransitionDungeonInterior(null, new StaticDoor(), location, true);
            playerEnterExit.StartCoroutine(WaitForMultiplayerVampireCemeteryWake(playerEnterExit, location, "VampireMP-host-final-enter-marker"));
        }

        private IEnumerator WaitForMultiplayerVampireCemeteryWake(PlayerEnterExit playerEnterExit, DFLocation location, string reason)
        {
            const float timeoutSeconds = 120f;
            float timeout = Time.realtimeSinceStartup + timeoutSeconds;

            while (Time.realtimeSinceStartup < timeout)
            {
                if (playerEnterExit != null && !playerEnterExit.IsRespawning &&
                    playerEnterExit.TryMovePlayerToDungeonEnterMarkerForMultiplayerWake(location, reason))
                {
                    ForceSendLocalCoordinates(reason);
                    yield break;
                }

                yield return null;
            }

            Debug.LogError($"[VampireMP] Timed out waiting for cemetery network dungeon EnterMarker. dungeon='{location.RegionName}/{location.Name}' reason={reason}");
        }

        private void ForceSendLocalCoordinates(string reason)
        {
            try
            {
                PlayerMultiplayer localPlayer = PlayerMultiplayer.GetLocalPlayer();
                if (localPlayer == null)
                    return;

                GameObject playerObject = GameManager.Instance != null ? GameManager.Instance.PlayerObject : null;
                if (playerObject != null)
                    localPlayer.transform.position = playerObject.transform.position;

                PositionMultiplayer positionMultiplayer = localPlayer.GetComponent<PositionMultiplayer>();
                if (positionMultiplayer != null)
                    positionMultiplayer.ForceSendCurrentCoordinatesNow(reason);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[VampireMP] Failed to force-send coordinates. reason={reason} error={ex.Message}");
            }
        }

        DFLocation GetRandomCemetery()
        {
            // Get player region data
            int regionIndex = GameManager.Instance.PlayerGPS.CurrentRegionIndex;
            DFRegion regionData = DaggerfallUnity.Instance.ContentReader.MapFileReader.GetRegion(regionIndex);

            // Collect all cemetery locations
            List<int> foundLocationIndices = new List<int>();
            for (int i = 0; i < regionData.LocationCount; i++)
            {
                if (((int)regionData.MapTable[i].DungeonType) == (int)DFRegion.DungeonTypes.Cemetery)
                    foundLocationIndices.Add(i);
            }

            // Select one at random
            int index = UnityEngine.Random.Range(0, foundLocationIndices.Count);
            DFLocation location = DaggerfallUnity.Instance.ContentReader.MapFileReader.GetLocation(regionIndex, foundLocationIndices[index]);
            if (!location.Loaded)
                throw new System.Exception("VampirismInfection.GetRandomCemetery() could not find a cemetery in this region.");

            return location;
        }

        #endregion

        #region Serialization

        [fsObject("v1")]
        public struct CustomSaveData_v1
        {
            public bool warningDreamVideoPlayed;
            public bool fakeDeathVideoPlayed;
            public uint startingDay;
            public int infectionRegionIndex;
        }

        protected override object GetCustomDiseaseSaveData()
        {
            CustomSaveData_v1 data = new CustomSaveData_v1();
            data.warningDreamVideoPlayed = warningDreamVideoPlayed;
            data.fakeDeathVideoPlayed = fakeDeathVideoPlayed;
            data.startingDay = startingDay;
            data.infectionRegionIndex = infectionRegionIndex;

            return data;
        }

        protected override void RestoreCustomDiseaseSaveData(object dataIn)
        {
            if (dataIn == null)
                return;

            CustomSaveData_v1 data = (CustomSaveData_v1)dataIn;
            warningDreamVideoPlayed = data.warningDreamVideoPlayed;
            fakeDeathVideoPlayed = data.fakeDeathVideoPlayed;
            startingDay = data.startingDay;
            infectionRegionIndex = data.infectionRegionIndex;
        }

        #endregion
    }
}