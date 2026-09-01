// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2023 Daggerfall Workshop
// Web Site:        http://www.dfworkshop.net
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Source Code:     https://github.com/Interkarma/daggerfall-unity
// Original Author: Lypyl (lypyl@dfworkshop.net)
// Contributors:    
// 
// Notes:
//

using System;
using System.Collections;
using UnityEngine;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallConnect.Utility;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Utility;
using DaggerfallConnect;

namespace DaggerfallWorkshop.Game.UserInterfaceWindows
{

    public class DaggerfallTravelPopUp : DaggerfallPopupWindow
    {
        #region fields
        DaggerfallTravelMapWindow travelWindow = null;

        const string nativeImgName = "TRAV0I04.IMG";

        const float secondsCountdownTickFastTravel = 0.05f; // time used for fast travel countdown for one tick
        protected TravelTimeCalculator travelTimeCalculator = new TravelTimeCalculator();

        Color32 toggleColor = new Color32(85, 117, 48, 255);
        const string greenCheckboxTextureFilename = "GreenCheckbox";

        Panel travelPanel;
        Panel speedToggleColorPanel;
        Panel transportToggleColorPanel;
        Panel sleepToggleColorPanel;

        protected Button beginButton;
        protected Button exitButton;
        protected Button cautiousToggleButton;
        protected Button recklessToggleButton;
        protected Button footHorseToggleButton;
        protected Button shipToggleButton;
        protected Button campOutToggleButton;
        protected Button innToggleButton;
        Texture2D nativeTexture;
        Texture2D greenCheckboxTexture;

        //rects
        Rect nativePanelRect        = new Rect(49, 28, 223, 97);
        Rect exitButtonRect         = new Rect(222, 112, 48, 10);
        Rect beginButtonRect        = new Rect(222, 98, 48, 10);
        Rect cautiousButtonRect     = new Rect(50, 51, 108, 9);
        Rect recklessButtonRect     = new Rect(50, 61, 108, 9);
        Rect footHorseButtonRect    = new Rect(163, 51, 108, 9);
        Rect shipButtonRect         = new Rect(163, 61, 108, 9);
        Rect innsButtonRect         = new Rect(50, 83, 108, 9);
        Rect campoutButtonRect      = new Rect(163, 83, 108, 9);

        Vector2 colorPanelSize      = new Vector2(4.75f, 4.75f);

        Vector2 cautiousPanelPos    = new Vector2(52.25f, 53);
        Vector2 recklessPanelPos    = new Vector2(52.25f, 63.25f);
        Vector2 innPanelPos         = new Vector2(52.25f, 85.5f);
        Vector2 campoutPos          = new Vector2(165, 85.5f);
        Vector2 footPos             = new Vector2(165, 53);
        Vector2 shipPos             = new Vector2(165, 63.25f);
        DFPosition endPos           = new DFPosition(109, 158);

        // Optional multiplayer party-rendezvous context. Normal travel-map trips leave
        // these disabled and retain the original singleplayer behaviour.
        bool hasTravelStartOverride = false;
        DFPosition travelStartOverride;
        bool hasExactDestinationWorldCoordinates = false;
        int exactDestinationWorldX = 0;
        int exactDestinationWorldZ = 0;
        bool hasExactDestinationWorldY = false;
        float exactDestinationWorldY = 0f;
        // Dungeon party targets should use DFU's normal exterior dungeon-door placement.
        // That routine resolves the actual entrance mesh height after the location is loaded,
        // which is safer than exact X/Z with terrain-only Y at raised stair/platform entrances.
        bool useDungeonEntranceReposition = false;

        // Dungeon party travel still performs the normal exterior fast travel first.
        // Once the destination exterior is ready, enter the already-synced network
        // dungeon and move near the target's live NetworkTransform position.
        bool usePartyDungeonRendezvous = false;
        global::PlayerMultiplayer partyDungeonTargetPlayer = null;
        string partyDungeonInstanceId = string.Empty;
        // Off-map and teleport-only dungeons have no exterior terrain destination.
        // In this mode the popup keeps its confirmation flow, then skips exterior
        // teleport and enters the already-synchronized exact dungeon instance.
        bool useDirectPartyDungeonRendezvous = false;

        bool closeSourceWindowAfterTravel = false;

        protected TextLabel availableGoldLabel;
        protected TextLabel tripCostLabel;
        protected TextLabel travelTimeLabel;

        protected int travelTimeTotalMins;
        protected int countdownValueTravelTimeDays; // used for remaining days in fast travel countdown
        protected bool doFastTravel = false; // flag used to indicate Update() function that fast travel should happen
        protected float waitTimer = 0;

        bool isCloseWindowDeferred = false;

        bool speedCautious  = true;
        bool travelShip     = true;
        bool sleepModeInn   = true;

        bool hasHorse = false;
        bool hasCart = false;
        bool hasShip = false;

        #endregion

        #region Properties

        public DFPosition EndPos { get { return endPos; } protected internal set { endPos = value;} }
        public DaggerfallTravelMapWindow TravelWindow { get { return travelWindow; } protected internal set { travelWindow = value; } }
        public bool SpeedCautious { get { return speedCautious;} set {speedCautious = value; } }
        public bool TravelShip { get { return travelShip;} set { travelShip = value;} }
        public bool SleepModeInn { get { return sleepModeInn; } set { sleepModeInn = value; } }

        /// <summary>
        /// Configures a normal fast-travel popup opened from the multiplayer party window.
        /// Travel time and cost start at the supplied exterior/dungeon-anchor map pixel.
        /// Exact synced world-coordinate arrival can be enabled for exterior players and
        /// network-dungeon entrance anchors. Building interiors use the normal safe map-pixel
        /// arrival because their exterior door position is not currently synchronized.
        /// </summary>
        public void ConfigurePartyTravel(
            DFPosition startMapPixel,
            int destinationWorldX,
            int destinationWorldZ,
            bool useExactDestinationWorldCoordinates,
            bool closeSourceWindowAfterTravel = true,
            bool useExactDestinationWorldY = false,
            float destinationWorldY = 0f,
            bool useDungeonEntranceReposition = false,
            bool usePartyDungeonRendezvous = false,
            global::PlayerMultiplayer partyDungeonTargetPlayer = null,
            string partyDungeonInstanceId = null,
            bool useDirectPartyDungeonRendezvous = false)
        {
            hasTravelStartOverride = true;
            travelStartOverride = startMapPixel;
            hasExactDestinationWorldCoordinates =
                useExactDestinationWorldCoordinates && destinationWorldX > 0 && destinationWorldZ > 0;
            exactDestinationWorldX = destinationWorldX;
            exactDestinationWorldZ = destinationWorldZ;
            hasExactDestinationWorldY =
                hasExactDestinationWorldCoordinates &&
                useExactDestinationWorldY &&
                !float.IsNaN(destinationWorldY) &&
                !float.IsInfinity(destinationWorldY);
            exactDestinationWorldY = hasExactDestinationWorldY ? destinationWorldY : 0f;
            bool validDungeonRendezvous =
                usePartyDungeonRendezvous &&
                partyDungeonTargetPlayer != null &&
                !string.IsNullOrEmpty(partyDungeonInstanceId);

            this.useDirectPartyDungeonRendezvous =
                validDungeonRendezvous && useDirectPartyDungeonRendezvous;
            this.useDungeonEntranceReposition =
                useDungeonEntranceReposition && !this.useDirectPartyDungeonRendezvous;
            this.usePartyDungeonRendezvous =
                validDungeonRendezvous &&
                (this.useDungeonEntranceReposition || this.useDirectPartyDungeonRendezvous);
            this.partyDungeonTargetPlayer = this.usePartyDungeonRendezvous ? partyDungeonTargetPlayer : null;
            this.partyDungeonInstanceId = this.usePartyDungeonRendezvous ? partyDungeonInstanceId : string.Empty;
            this.closeSourceWindowAfterTravel = closeSourceWindowAfterTravel;
        }

        void ClearPartyTravelConfiguration()
        {
            hasTravelStartOverride = false;
            travelStartOverride = new DFPosition();
            hasExactDestinationWorldCoordinates = false;
            exactDestinationWorldX = 0;
            exactDestinationWorldZ = 0;
            hasExactDestinationWorldY = false;
            exactDestinationWorldY = 0f;
            useDungeonEntranceReposition = false;
            usePartyDungeonRendezvous = false;
            partyDungeonTargetPlayer = null;
            partyDungeonInstanceId = string.Empty;
            useDirectPartyDungeonRendezvous = false;
            closeSourceWindowAfterTravel = false;
        }

        #endregion

        #region constructors

        public DaggerfallTravelPopUp(IUserInterfaceManager uiManager, IUserInterfaceWindow previousWindow = null, DaggerfallTravelMapWindow travelWindow = null)
            : base(uiManager, previousWindow)
        {
            this.travelWindow = travelWindow;
        }

        #endregion


        #region User InterFace

        protected override void Setup()
        {
            base.Setup();

            nativeTexture = DaggerfallUI.GetTextureFromImg(nativeImgName);
            if (!nativeTexture)
                throw new System.Exception("DaggerfallTravelMap: Could not load native texture.");

            greenCheckboxTexture = DaggerfallUI.GetTextureFromResources(greenCheckboxTextureFilename);

            ParentPanel.BackgroundColor = Color.clear;

            travelPanel = DaggerfallUI.AddPanel(nativePanelRect, NativePanel);
            travelPanel.BackgroundTexture = nativeTexture;

            availableGoldLabel = DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(148, 97), "0", NativePanel);
            availableGoldLabel.MaxCharacters = 12;

            tripCostLabel = DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(117, 107), "0", NativePanel);
            tripCostLabel.MaxCharacters = 18;

            travelTimeLabel = DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, new Vector2(129, 117), "0", NativePanel);
            travelTimeLabel.MaxCharacters = 16;

            speedToggleColorPanel = DaggerfallUI.AddPanel(new Rect(cautiousPanelPos, colorPanelSize), NativePanel);
            SetToggleLook(speedToggleColorPanel);

            sleepToggleColorPanel = DaggerfallUI.AddPanel(new Rect(innPanelPos, colorPanelSize), NativePanel);
            SetToggleLook(sleepToggleColorPanel);

            transportToggleColorPanel = DaggerfallUI.AddPanel(new Rect(footPos, colorPanelSize), NativePanel);
            SetToggleLook(transportToggleColorPanel);

            SetupButtons();
            Refresh();
        }

        private void SetToggleLook(Panel toggle)
        {
            if (greenCheckboxTexture)
                toggle.BackgroundTexture = greenCheckboxTexture;
            else
                toggle.BackgroundColor = toggleColor;
        }

        void SetupButtons()
        {
            beginButton = DaggerfallUI.AddButton(beginButtonRect, NativePanel );
            beginButton.OnMouseClick += BeginButtonOnClickHandler;
            beginButton.Hotkey = DaggerfallShortcut.GetBinding(DaggerfallShortcut.Buttons.TravelBegin);

            exitButton = DaggerfallUI.AddButton(exitButtonRect, NativePanel);
            exitButton.OnMouseClick += ExitButtonOnClickHandler;
            exitButton.Hotkey = DaggerfallShortcut.GetBinding(DaggerfallShortcut.Buttons.TravelExit);
            exitButton.OnKeyboardEvent += ExitButton_OnKeyboardEvent;

            cautiousToggleButton = DaggerfallUI.AddButton(cautiousButtonRect, NativePanel);
            cautiousToggleButton.OnMouseClick += SpeedButtonOnClickHandler;
            cautiousToggleButton.Hotkey = DaggerfallShortcut.GetBinding(DaggerfallShortcut.Buttons.TravelSpeedToggle);
            cautiousToggleButton.OnKeyboardEvent += SpeedButton_OnKeyboardEvent;
            cautiousToggleButton.OnMouseScrollUp += ToggleSpeedButtonOnScrollHandler;
            cautiousToggleButton.OnMouseScrollDown += ToggleSpeedButtonOnScrollHandler;

            recklessToggleButton = DaggerfallUI.AddButton(recklessButtonRect, NativePanel);
            recklessToggleButton.OnMouseClick += SpeedButtonOnClickHandler;
            recklessToggleButton.OnMouseScrollUp += ToggleSpeedButtonOnScrollHandler;
            recklessToggleButton.OnMouseScrollDown += ToggleSpeedButtonOnScrollHandler;

            footHorseToggleButton = DaggerfallUI.AddButton(footHorseButtonRect, NativePanel);
            footHorseToggleButton.OnMouseClick += TransportModeButtonOnClickHandler;
            footHorseToggleButton.Hotkey = DaggerfallShortcut.GetBinding(DaggerfallShortcut.Buttons.TravelTransportModeToggle);
            footHorseToggleButton.OnKeyboardEvent += TransportModeButtonOnKeyboardHandler;
            footHorseToggleButton.OnMouseScrollUp += ToggleTransportModeButtonOnScrollHandler;
            footHorseToggleButton.OnMouseScrollDown += ToggleTransportModeButtonOnScrollHandler;

            shipToggleButton = DaggerfallUI.AddButton(shipButtonRect, NativePanel);
            shipToggleButton.OnMouseClick += TransportModeButtonOnClickHandler;
            shipToggleButton.OnMouseScrollUp += ToggleTransportModeButtonOnScrollHandler;
            shipToggleButton.OnMouseScrollDown += ToggleTransportModeButtonOnScrollHandler;

            innToggleButton = DaggerfallUI.AddButton(innsButtonRect, NativePanel);
            innToggleButton.OnMouseClick += SleepModeButtonOnClickHandler;
            innToggleButton.Hotkey = DaggerfallShortcut.GetBinding(DaggerfallShortcut.Buttons.TravelInnCampOutToggle);
            innToggleButton.OnKeyboardEvent += SleepModeButtonOnKeyboardandler;
            innToggleButton.OnMouseScrollUp += ToggleSleepModeButtonOnScrollHandler;
            innToggleButton.OnMouseScrollDown += ToggleSleepModeButtonOnScrollHandler;

            campOutToggleButton = DaggerfallUI.AddButton(campoutButtonRect, NativePanel);
            campOutToggleButton.OnMouseClick += SleepModeButtonOnClickHandler;
            campOutToggleButton.OnMouseScrollUp += ToggleSleepModeButtonOnScrollHandler;
            campOutToggleButton.OnMouseScrollDown += ToggleSleepModeButtonOnScrollHandler;
        }


        public override void OnPush()
        {
            base.OnPush();

            Items.ItemCollection inventory = GameManager.Instance.PlayerEntity.Items;
            hasHorse = inventory.Contains(Items.ItemGroups.Transportation, (int)Items.Transportation.Horse);
            hasCart = inventory.Contains(Items.ItemGroups.Transportation, (int)Items.Transportation.Small_cart);
            hasShip = Banking.DaggerfallBankManager.OwnsShip || GameManager.Instance.GuildManager.FreeShipTravel();

            if (base.IsSetup)
                Refresh();
        }

        public override void OnPop()
        {
            base.OnPop();
            ClearPartyTravelConfiguration();
        }

        #endregion

        #region Overrides

        public override void Update()
        {
            base.Update();

            if (doFastTravel)
            {
                if (countdownValueTravelTimeDays > 0)
                {
                    TickCountdown();
                }
                else
                {
                    doFastTravel = false;
                    DaggerfallUI.Instance.FadeBehaviour.SmashHUDToBlack();
                    performFastTravel();
                }
            }
        }

        #endregion


        #region Methods

        //Update when player pushes buttons etc.
        protected virtual void Refresh()
        {
            UpdateTogglePanels();
            UpdateLabels();
        }

        //Updates the positions for the panels to indicate which button is selected
        protected virtual void UpdateTogglePanels()
        {
            if (speedCautious)
                speedToggleColorPanel.Position = cautiousPanelPos;
            else
                speedToggleColorPanel.Position = recklessPanelPos;
            if (sleepModeInn)
                sleepToggleColorPanel.Position = innPanelPos;
            else
                sleepToggleColorPanel.Position = campoutPos;
            if (travelShip)
                transportToggleColorPanel.Position = shipPos;
            else
                transportToggleColorPanel.Position = footPos;
        }

        //Updates text labels
        protected virtual void UpdateLabels()
        {
            availableGoldLabel.Text = GameManager.Instance.PlayerEntity.GoldPieces.ToString();
            if (hasTravelStartOverride)
            {
                travelTimeTotalMins = travelTimeCalculator.CalculateTravelTimeFromPosition(
                    travelStartOverride,
                    endPos,
                    speedCautious,
                    sleepModeInn,
                    travelShip,
                    hasHorse,
                    hasCart);
            }
            else
            {
                travelTimeTotalMins = travelTimeCalculator.CalculateTravelTime(
                    endPos,
                    speedCautious,
                    sleepModeInn,
                    travelShip,
                    hasHorse,
                    hasCart);
            }

            // Players can have fast travel benefit from guild memberships
            travelTimeTotalMins = GameManager.Instance.GuildManager.FastTravel(travelTimeTotalMins);

            int travelTimeDaysTotal = (travelTimeTotalMins / 1440);

            // Classic always adds 1. For DF Unity, only add 1 if there is a remainder to round up.
            if ((travelTimeTotalMins % 1440) > 0)
                travelTimeDaysTotal += 1;

            travelTimeCalculator.CalculateTripCost(
                travelTimeTotalMins,
                sleepModeInn,
                hasShip,
                travelShip
                );

            travelTimeLabel.Text = string.Format("{0}", travelTimeDaysTotal);
            tripCostLabel.Text = travelTimeCalculator.TotalCost.ToString();

            countdownValueTravelTimeDays = travelTimeDaysTotal;
        }

        protected virtual bool TickCountdown()
        {
            bool finished = false;

            if (Time.realtimeSinceStartup > waitTimer + secondsCountdownTickFastTravel)
            {
                waitTimer = Time.realtimeSinceStartup;

                countdownValueTravelTimeDays--;
                travelTimeLabel.Text = string.Format("{0}", countdownValueTravelTimeDays);
                travelTimeLabel.Update();

                finished = true;
            }

            return finished;
        }

        // perform fast travel actions
        private void performFastTravel()
        {
            // Capture every party-only value before closing the UI stack. PopToHUD() removes
            // this popup and calls OnPop(), which deliberately clears party-travel state.
            bool isPartyTravel = hasTravelStartOverride;
            bool useExactDestinationWorldCoordinates = hasExactDestinationWorldCoordinates;
            int destinationWorldX = exactDestinationWorldX;
            int destinationWorldZ = exactDestinationWorldZ;
            bool useExactDestinationWorldY = hasExactDestinationWorldY;
            float destinationWorldY = exactDestinationWorldY;
            bool repositionAtDungeonEntrance = useDungeonEntranceReposition;
            bool rendezvousInsidePartyDungeon = usePartyDungeonRendezvous;
            bool directPartyDungeonRendezvous = useDirectPartyDungeonRendezvous;

            // Capture the source dungeon state before PrepareExteriorStateForPartyTravel()
            // clears it for normal party travel. Party travel must not become a free
            // cautious-travel heal when either end of the trip is a dungeon.
            bool startedInsideDungeon =
                isPartyTravel &&
                GameManager.Instance != null &&
                GameManager.Instance.PlayerEnterExit != null &&
                GameManager.Instance.PlayerEnterExit.IsPlayerInsideDungeon;
            bool suppressPartyDungeonHealthRestore =
                isPartyTravel && (startedInsideDungeon || rendezvousInsidePartyDungeon);

            global::PlayerMultiplayer dungeonTargetPlayer = partyDungeonTargetPlayer;
            string expectedDungeonInstanceId = partyDungeonInstanceId;
            bool closePartySourceWindow = closeSourceWindowAfterTravel;
            bool suppressLocalTravelTime = global::TimeCatcher.IsPureClientUsingHostTime;

            // The vanilla travel map is exterior-only. Normal party travel therefore
            // restores exterior state first. A direct off-map rendezvous can switch from
            // one live network dungeon to another without constructing either exterior;
            // building interiors still exit through their multiplayer-safe path.
            PrepareExteriorStateForPartyTravel(directPartyDungeonRendezvous);

            if (isPartyTravel && DaggerfallUI.Instance != null)
            {
                // Party travel is launched from ESC -> pause menu -> party journal -> travel
                // popup. SmashHUDToBlack() only covers the gameplay HUD, not those windows.
                // Remove the entire menu stack before starting StreamingWorld.InitWorld(),
                // so the already-black HUD is exposed before any destination construction.
                DaggerfallUI.Instance.PopToHUD();
            }

            DeductFastTravelGold();

            RaiseOnPreFastTravelEvent();

            // Cache scene first, if fast travelling while on ship.
            if (GameManager.Instance.TransportManager.IsOnShip())
                SaveLoadManager.CacheScene(GameManager.Instance.StreamingWorld.SceneName);
            GameManager.Instance.StreamingWorld.RestoreWorldCompensationHeight(0);
            if (directPartyDungeonRendezvous)
            {
                // The target dungeon has no streamable exterior map pixel. Do not
                // clamp its coordinates or build unrelated terrain. The rendezvous
                // coroutine below will enter the already-synchronized exact instance.
                Debug.Log($"[PartyFastTravel] Direct off-map dungeon rendezvous. instance='{expectedDungeonInstanceId}'");
            }
            else if (repositionAtDungeonEntrance)
            {
                // Use the normal DFU dungeon-exterior placement after this location has
                // streamed in. It finds the vertically lowest dungeon entrance door, moves
                // outward by the controller radius, and preserves the real door/platform Y.
                GameManager.Instance.StreamingWorld.TeleportToCoordinates(
                    (int)endPos.X,
                    (int)endPos.Y,
                    StreamingWorld.RepositionMethods.DungeonEntrance);
            }
            else if (useExactDestinationWorldCoordinates)
            {
                if (useExactDestinationWorldY)
                {
                    GameManager.Instance.StreamingWorld.TeleportToWorldCoordinates(
                        destinationWorldX,
                        destinationWorldZ,
                        destinationWorldY);
                }
                else
                {
                    GameManager.Instance.StreamingWorld.TeleportToWorldCoordinates(
                        destinationWorldX,
                        destinationWorldZ);
                }
            }
            else
            {
                GameManager.Instance.StreamingWorld.TeleportToCoordinates(
                    (int)endPos.X,
                    (int)endPos.Y,
                    StreamingWorld.RepositionMethods.DirectionFromStartMarker);
            }

            // Direct off-map rendezvous represents an immediate network-interior
            // handoff, not elapsed overland travel. Do not turn its zero-distance
            // popup into a free heal/rest operation.
            if (!directPartyDungeonRendezvous && speedCautious)
            {
                // Preserve vanilla/SP and ordinary exterior party-travel healing, but do not
                // refill health when party travel starts in or rendezvous-travels to a dungeon.
                // Fatigue and magicka keep their existing cautious-travel behaviour.
                if (!suppressPartyDungeonHealthRestore)
                    GameManager.Instance.PlayerEntity.CurrentHealth = GameManager.Instance.PlayerEntity.MaxHealth;

                GameManager.Instance.PlayerEntity.CurrentFatigue = GameManager.Instance.PlayerEntity.MaxFatigue;
                if (!GameManager.Instance.PlayerEntity.Career.NoRegenSpellPoints)
                    GameManager.Instance.PlayerEntity.CurrentMagicka = GameManager.Instance.PlayerEntity.MaxMagicka;
            }

            // Pure clients keep the host's authoritative clock. Travel duration remains
            // calculated and is still used for the countdown, price, and all other effects.
            if (!directPartyDungeonRendezvous && !suppressLocalTravelTime)
                DaggerfallUnity.WorldTime.DaggerfallDateTime.RaiseTime(travelTimeTotalMins * 60);

            // Halt random enemy spawns for next playerEntity update so player isn't bombarded by spawned enemies at the end of a long trip
            if (!directPartyDungeonRendezvous)
                GameManager.Instance.PlayerEntity.PreventEnemySpawns = true;

            // Arrival-time normalization is also a world-time change. Keep the original
            // vampire/sunlight and cautious-travel behaviour for SP, the host, and clients
            // not using host-authoritative time, but never move a pure client's clock.
            if (!directPartyDungeonRendezvous && !suppressLocalTravelTime)
            {
                // Vampires and characters with Damage from Sunlight disadvantage never arrive between 6am and 6pm regardless of travel type
                // Otherwise raise arrival time to just after 7am if cautious travel would arrive at night
                if (GameManager.Instance.PlayerEffectManager.HasVampirism() || GameManager.Instance.PlayerEntity.Career.DamageFromSunlight)
                {
                    if (DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.IsDay)
                    {
                        DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.RaiseTime(
                            (DaggerfallDateTime.DuskHour - DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.Hour) * 3600);
                    }
                }
                else if (speedCautious)
                {
                    if ((DaggerfallUnity.WorldTime.DaggerfallDateTime.Hour < 7)
                        || ((DaggerfallUnity.WorldTime.DaggerfallDateTime.Hour == 7) && (DaggerfallUnity.WorldTime.DaggerfallDateTime.Minute < 10)))
                    {
                        float raiseTime = (((7 - DaggerfallUnity.WorldTime.DaggerfallDateTime.Hour) * 3600)
                                            + ((10 - DaggerfallUnity.WorldTime.DaggerfallDateTime.Minute) * 60)
                                            - DaggerfallUnity.WorldTime.DaggerfallDateTime.Second);
                        DaggerfallUnity.WorldTime.DaggerfallDateTime.RaiseTime(raiseTime);
                    }
                    else if (DaggerfallUnity.WorldTime.DaggerfallDateTime.Hour > 17)
                    {
                        float raiseTime = (((31 - DaggerfallUnity.WorldTime.DaggerfallDateTime.Hour) * 3600)
                        + ((10 - DaggerfallUnity.WorldTime.DaggerfallDateTime.Minute) * 60)
                        - DaggerfallUnity.WorldTime.DaggerfallDateTime.Second);
                        DaggerfallUnity.WorldTime.DaggerfallDateTime.RaiseTime(raiseTime);
                    }
                }
            }

            if (!isPartyTravel)
            {
                // Preserve the original travel-map window handling exactly for normal SP travel.
                DaggerfallUI.Instance.UserInterfaceManager.PopWindow();

                if (travelWindow != null)
                {
                    travelWindow.CloseTravelWindows(true);
                }
                else if (closePartySourceWindow)
                {
                    // Compatibility for a non-party caller that directly opens this popup.
                    DaggerfallUI.Instance.UserInterfaceManager.PopWindow();
                }
            }

            if (!directPartyDungeonRendezvous)
                GameManager.Instance.PlayerEntity.RaiseSkills();

            if (isPartyTravel && GameManager.Instance != null)
            {
                // TeleportToCoordinates()/TeleportToWorldCoordinates() starts an asynchronous
                // StreamingWorld rebuild. The menu stack has already been removed above, so
                // keep the exposed HUD black until terrain and exterior locations are ready.
                GameManager.Instance.StartCoroutine(FadeInAfterPartyTravelWorldReady(
                    rendezvousInsidePartyDungeon,
                    dungeonTargetPlayer,
                    expectedDungeonInstanceId));
            }
            else
            {
                // Preserve the original singleplayer travel-map timing exactly.
                DaggerfallUI.Instance.FadeBehaviour.FadeHUDFromBlack();
            }

            RaiseOnPostFastTravelEvent();
        }

        IEnumerator FadeInAfterPartyTravelWorldReady(
            bool rendezvousInsidePartyDungeon,
            global::PlayerMultiplayer dungeonTargetPlayer,
            string expectedDungeonInstanceId)
        {
            StreamingWorld streamingWorld =
                GameManager.Instance != null ? GameManager.Instance.StreamingWorld : null;

            float timeoutAt = Time.realtimeSinceStartup + 20f;

            // StreamingWorld exposes the complete teleport rebuild state: initialisation,
            // terrain jobs, and the final exterior-location refresh. Keep a timeout so a
            // separate streaming failure cannot leave the player's HUD permanently black.
            while (streamingWorld != null &&
                   streamingWorld.IsWorldUpdateRunning &&
                   Time.realtimeSinceStartup < timeoutAt)
            {
                yield return null;
            }

            // Give location/building renderers one final frame to enable after init clears.
            yield return new WaitForEndOfFrame();

            if (rendezvousInsidePartyDungeon)
            {
                // Drive the rendezvous iterator manually so an exception after
                // TransitionDungeonInterior() has already moved the player to the
                // StartMarker cannot abort this outer coroutine and strand the HUD black.
                IEnumerator rendezvousRoutine = EnterPartyDungeonAndRendezvous(
                    dungeonTargetPlayer,
                    expectedDungeonInstanceId);

                while (rendezvousRoutine != null)
                {
                    bool hasNext = false;
                    object yieldedValue = null;

                    try
                    {
                        hasNext = rendezvousRoutine.MoveNext();
                        if (hasNext)
                            yieldedValue = rendezvousRoutine.Current;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[PartyFastTravel] Dungeon rendezvous failed after arrival. Fade-in will still complete. instance='{expectedDungeonInstanceId}' error={ex}");
                        ShowPartyTravelArrivalMessage("Dungeon entry completed, but party rendezvous encountered an error.");
                        break;
                    }

                    if (!hasNext)
                        break;

                    yield return yieldedValue;
                }
            }

            if (DaggerfallUI.Instance != null && DaggerfallUI.Instance.FadeBehaviour != null)
                DaggerfallUI.Instance.FadeBehaviour.FadeHUDFromBlack(0.7f);
        }

        IEnumerator EnterPartyDungeonAndRendezvous(
            global::PlayerMultiplayer targetPlayer,
            string expectedDungeonInstanceId)
        {
            const float waitTimeoutSeconds = 25f;
            float timeoutAt = Time.realtimeSinceStartup + waitTimeoutSeconds;
            DaggerfallDungeon targetDungeon = null;

            // Every active network dungeon is already synchronized to all clients.
            // Wait for this client's copy of the exact target instance to finish layout.
            while (Time.realtimeSinceStartup < timeoutAt)
            {
                if (!IsPartyDungeonTargetStillValid(targetPlayer, expectedDungeonInstanceId))
                {
                    ShowPartyTravelArrivalMessage("The party member is no longer inside that dungeon.");
                    yield break;
                }

                DaggerfallDungeon[] dungeons = UnityEngine.Object.FindObjectsOfType<DaggerfallDungeon>();
                for (int i = 0; i < dungeons.Length; i++)
                {
                    DaggerfallDungeon candidate = dungeons[i];
                    if (candidate == null ||
                        !candidate.IsNetworkDungeonInstance ||
                        !string.Equals(candidate.DungeonInstanceId, expectedDungeonInstanceId, StringComparison.Ordinal))
                        continue;

                    targetDungeon = candidate;
                    break;
                }

                if (targetDungeon != null && targetDungeon.isSet && targetDungeon.StartMarker != null)
                    break;

                targetDungeon = null;
                yield return null;
            }

            if (targetDungeon == null)
            {
                ShowPartyTravelArrivalMessage("The shared dungeon was not ready. You arrived at its entrance.");
                yield break;
            }

            PlayerEnterExit enterExit =
                GameManager.Instance != null ? GameManager.Instance.PlayerEnterExit : null;
            if (enterExit == null)
            {
                ShowPartyTravelArrivalMessage("Could not enter the shared dungeon.");
                yield break;
            }

            DFLocation location = targetDungeon.Summary.LocationData;
            if (!location.Loaded)
            {
                location = DaggerfallUnity.Instance.ContentReader.MapFileReader.GetLocation(
                    targetDungeon.Summary.RegionName,
                    targetDungeon.Summary.LocationName);
            }

            if (!location.Loaded)
            {
                ShowPartyTravelArrivalMessage("Could not load the shared dungeon location.");
                yield break;
            }

            // This reuses the current multiplayer dungeon transition. Because the exact
            // instance is already present locally, it enters that object and first moves
            // to its normal StartMarker. No dungeon is manually constructed or Y-offset here.
            //
            // Quest-resource/action callbacks can throw after the transition has already
            // marked the player inside and moved to StartMarker. Treat that as a recoverable
            // post-entry error: validate the resulting dungeon below and continue to the
            // final party-member snap when the correct instance was actually entered.
            try
            {
                enterExit.TransitionDungeonInterior(null, new StaticDoor(), location, false);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PartyFastTravel] TransitionDungeonInterior threw during rendezvous. Checking whether entry already completed. instance='{expectedDungeonInstanceId}' error={ex}");
            }

            while (Time.realtimeSinceStartup < timeoutAt)
            {
                DaggerfallDungeon enteredDungeon = enterExit.Dungeon;
                if (enterExit.IsPlayerInsideDungeon &&
                    enteredDungeon != null &&
                    enteredDungeon.IsNetworkDungeonInstance &&
                    string.Equals(enteredDungeon.DungeonInstanceId, expectedDungeonInstanceId, StringComparison.Ordinal))
                {
                    break;
                }

                yield return null;
            }

            if (!enterExit.IsPlayerInsideDungeon ||
                enterExit.Dungeon == null ||
                !string.Equals(enterExit.Dungeon.DungeonInstanceId, expectedDungeonInstanceId, StringComparison.Ordinal))
            {
                ShowPartyTravelArrivalMessage("Could not complete entry into the shared dungeon.");
                yield break;
            }

            // Entry and NetworkTransform/party-state updates can settle on adjacent
            // frames. Retry the final snap briefly instead of treating the first false
            // result as permanent. TryMovePlayerNearPartyMemberInDungeon performs the
            // exact instance-ID validation on every attempt.
            float moveTimeoutAt = Time.realtimeSinceStartup + 3f;
            bool movedNearTarget = false;

            while (Time.realtimeSinceStartup < moveTimeoutAt)
            {
                if (!IsPartyDungeonTargetStillValid(targetPlayer, expectedDungeonInstanceId))
                {
                    ShowPartyTravelArrivalMessage("The party member left the dungeon before you arrived.");
                    yield break;
                }

                try
                {
                    movedNearTarget = enterExit.TryMovePlayerNearPartyMemberInDungeon(
                        targetPlayer.transform,
                        expectedDungeonInstanceId,
                        "party-fast-travel-rendezvous");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[PartyFastTravel] Final dungeon rendezvous move threw. Retrying until timeout. instance='{expectedDungeonInstanceId}' error={ex}");
                    movedNearTarget = false;
                }

                if (movedNearTarget)
                    break;

                yield return null;
            }

            if (!movedNearTarget)
                ShowPartyTravelArrivalMessage("Entered the dungeon, but could not move to the party member.");
        }

        static bool IsPartyDungeonTargetStillValid(
            global::PlayerMultiplayer targetPlayer,
            string expectedDungeonInstanceId)
        {
            if (targetPlayer == null || string.IsNullOrEmpty(expectedDungeonInstanceId))
                return false;

            global::PositionMultiplayer targetPosition =
                targetPlayer.GetComponent<global::PositionMultiplayer>();

            return targetPosition != null &&
                   targetPosition.PartyLocationShared &&
                   targetPosition.PartyCurrentLocationState ==
                       global::PositionMultiplayer.PartyLocationState.DungeonInterior &&
                   string.Equals(
                       targetPosition.PartyDungeonInstanceId,
                       expectedDungeonInstanceId,
                       StringComparison.Ordinal);
        }

        static void ShowPartyTravelArrivalMessage(string message)
        {
            if (string.IsNullOrEmpty(message) ||
                DaggerfallUI.Instance == null ||
                DaggerfallUI.Instance.DaggerfallHUD == null)
                return;

            DaggerfallUI.Instance.DaggerfallHUD.PopupText.AddText(message);
        }

        void PrepareExteriorStateForPartyTravel(bool directDungeonRendezvous)
        {
            if (!hasTravelStartOverride || GameManager.Instance == null)
                return;

            PlayerEnterExit enterExit = GameManager.Instance.PlayerEnterExit;
            if (enterExit == null)
                return;

            if (enterExit.IsPlayerInsideDungeon)
            {
                // Direct instance-to-instance rendezvous does not require an exterior.
                // This is important when the current dungeon is itself off-map and has
                // no safe exterior for EmergencyExitDungeonForNetworkChange(). The later
                // TransitionDungeonInterior call replaces the active dungeon reference.
                if (!directDungeonRendezvous)
                    enterExit.EmergencyExitDungeonForNetworkChange("party-fast-travel");
            }
            else if (enterExit.IsPlayerInsideBuilding)
            {
                enterExit.EmergencyExitBuildingForNetworkChange("party-fast-travel");
            }
        }

        // Return whether player has enough gold for the selected travel options
        // Taverns only accept gold pieces
        protected virtual bool enoughGoldCheck()
        {
            return (GameManager.Instance.PlayerEntity.GetGoldAmount() >= travelTimeCalculator.TotalCost) &&
                   (GameManager.Instance.PlayerEntity.GoldPieces >= travelTimeCalculator.PiecesCost);
        }

        protected virtual void showNotEnoughGoldPopup()
        {
            const int notEnoughGoldTextId = 454;

            TextFile.Token[] tokens = DaggerfallUnity.TextProvider.GetRSCTokens(notEnoughGoldTextId);
            if (tokens != null && tokens.Length > 0)
            {
                DaggerfallMessageBox messageBox = new DaggerfallMessageBox(uiManager, this);
                messageBox.SetTextTokens(tokens);
                messageBox.ClickAnywhereToClose = true;
                messageBox.Show();
            }
        }

        #endregion


        #region events

        public virtual void BeginButtonOnClickHandler(BaseScreenComponent sender, Vector2 position)
        {
            Refresh();

            DaggerfallUI.Instance.PlayOneShot(SoundClips.ButtonClick);
            // Warns player if they have a disease
            if (GameManager.Instance.PlayerEffectManager.DiseaseCount > 0 || GameManager.Instance.PlayerEffectManager.PoisonCount > 0)
            {
                DaggerfallMessageBox messageBox = new DaggerfallMessageBox(uiManager, this);
                TextFile.Token[] tokens = DaggerfallUnity.Instance.TextProvider.GetRandomTokens(1010);
                messageBox.SetTextTokens(tokens);
                messageBox.AddButton(DaggerfallMessageBox.MessageBoxButtons.Yes);
                messageBox.AddButton(DaggerfallMessageBox.MessageBoxButtons.No);
                messageBox.OnButtonClick += ConfirmTravelPopupDiseasedButtonClick;
                uiManager.PushWindow(messageBox);
            }
            else
            {
                CallFastTravelGoldCheck();
            }
        }

        public override void CancelWindow()
        {
            DaggerfallUI.Instance.PlayOneShot(SoundClips.ButtonClick);
            doFastTravel = false;
            base.CancelWindow();
        }

        /// <summary>
        /// Button handler for travel-with-incubating-disease confirmation pop up.
        /// </summary>
        protected virtual void ConfirmTravelPopupDiseasedButtonClick(DaggerfallMessageBox sender, DaggerfallMessageBox.MessageBoxButtons messageBoxButton)
        {
            DaggerfallUI.Instance.PlayOneShot(SoundClips.ButtonClick);
            sender.CloseWindow();

            if (messageBoxButton == DaggerfallMessageBox.MessageBoxButtons.Yes)
            {
                CallFastTravelGoldCheck();
            }
            else
                return;
        }

        protected virtual void CallFastTravelGoldCheck()
        {
            if (!enoughGoldCheck())
            {
                showNotEnoughGoldPopup();
                return;
            }
            
            doFastTravel = true; // initiate fast travel (Update() function will perform fast travel when this flag is true)
        }

        private void DeductFastTravelGold()
        {
            GameManager.Instance.PlayerEntity.GoldPieces -= travelTimeCalculator.PiecesCost;
            GameManager.Instance.PlayerEntity.DeductGoldAmount(travelTimeCalculator.TotalCost - travelTimeCalculator.PiecesCost);
        }

        public virtual void ExitButtonOnClickHandler(BaseScreenComponent sender, Vector2 position)
        {
            DaggerfallUI.Instance.PlayOneShot(SoundClips.ButtonClick);
            doFastTravel = false;
            DaggerfallUI.Instance.UserInterfaceManager.PopWindow();
        }

        protected virtual void ExitButton_OnKeyboardEvent(BaseScreenComponent sender, Event keyboardEvent)
        {
            if (keyboardEvent.type == EventType.KeyDown)
            {
                DaggerfallUI.Instance.PlayOneShot(SoundClips.ButtonClick);
                isCloseWindowDeferred = true;
            }
            else if (keyboardEvent.type == EventType.KeyUp && isCloseWindowDeferred)
            {
                isCloseWindowDeferred = false;
                doFastTravel = false;
                DaggerfallUI.Instance.UserInterfaceManager.PopWindow();
            }
        }

        public virtual void SpeedButtonOnClickHandler(BaseScreenComponent sender, Vector2 position)
        {
            DaggerfallUI.Instance.PlayOneShot(SoundClips.ButtonClick);

            speedCautious = (sender == cautiousToggleButton);
            Refresh();
        }

        public virtual void SpeedButton_OnKeyboardEvent(BaseScreenComponent sender, Event keyboardEvent)
        {
            if (keyboardEvent.type == EventType.KeyDown)
                ToggleSpeedButtonOnScrollHandler(sender);
        }

        public virtual void ToggleSpeedButtonOnScrollHandler(BaseScreenComponent sender)
        {
            DaggerfallUI.Instance.PlayOneShot(SoundClips.ButtonClick);
            speedCautious = !speedCautious;
            Refresh();
        }

        public virtual void TransportModeButtonOnClickHandler(BaseScreenComponent sender, Vector2 position)
        {
            DaggerfallUI.Instance.PlayOneShot(SoundClips.ButtonClick);
            travelShip = (sender == shipToggleButton);
            Refresh();
        }

        public virtual void TransportModeButtonOnKeyboardHandler(BaseScreenComponent sender, Event keyboardEvent)
        {
            if (keyboardEvent.type == EventType.KeyDown)
                ToggleTransportModeButtonOnScrollHandler(sender);
        }

        public virtual void ToggleTransportModeButtonOnScrollHandler(BaseScreenComponent sender)
        {
            DaggerfallUI.Instance.PlayOneShot(SoundClips.ButtonClick);
            travelShip = !travelShip;
            Refresh();
        }

        public virtual void SleepModeButtonOnClickHandler(BaseScreenComponent sender, Vector2 position)
        {
            DaggerfallUI.Instance.PlayOneShot(SoundClips.ButtonClick);
            sleepModeInn = (sender == innToggleButton);
            Refresh();
        }

        public virtual void SleepModeButtonOnKeyboardandler(BaseScreenComponent sender, Event keyboardEvent)
        {
            if (keyboardEvent.type == EventType.KeyDown)
                ToggleSleepModeButtonOnScrollHandler(sender);
        }

        public virtual void ToggleSleepModeButtonOnScrollHandler(BaseScreenComponent sender)
        {
            DaggerfallUI.Instance.PlayOneShot(SoundClips.ButtonClick);
            sleepModeInn = !sleepModeInn;
            Refresh();
        }

        /// <summary>
        /// Raised before a fast travel is performed.
        /// </summary>
        public static event Action<DaggerfallTravelPopUp> OnPreFastTravel;
        void RaiseOnPreFastTravelEvent()
        {
            if (OnPreFastTravel != null)
                OnPreFastTravel(this);
        }

        // OnPostFastTravel
        public delegate void OnOnPostFastTravelEventHandler();
        public static event OnOnPostFastTravelEventHandler OnPostFastTravel;
        void RaiseOnPostFastTravelEvent()
        {
            if (OnPostFastTravel != null)
                OnPostFastTravel();
        }

        #endregion

    }
}
