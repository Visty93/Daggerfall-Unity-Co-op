// Project:         Daggerfall Unity - TanguyMultiplayer detailed party window
// Notes:           Journal-style party roster opened from HudMultiplayer, with shared-location fast travel.

using System;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Utility;

namespace DaggerfallWorkshop.Game.UserInterfaceWindows
{
    /// <summary>
    /// Detailed multiplayer party roster using the classic quest-journal background.
    /// Displays all connected players, including the local player marked as "You".
    /// Shared remote locations can be clicked to open the normal fast-travel options.
    /// This does not alter the compact HUD party cards.
    /// </summary>
    public class DaggerfallMultiplayerPartyWindow : DaggerfallPopupWindow
    {
        const string journalBackgroundFilename = "LGBK00I0.IMG";
        const int membersPerPage = 4;
        const float refreshInterval = 0.25f;

        sealed class PartyMember
        {
            public uint key;
            public global::PlayerMultiplayer player;
            public global::PlayerAssets assets;
            public global::PositionMultiplayer position;
            public bool isLocalPlayer;
        }

        sealed class PartySlot
        {
            public Panel root;
            public Panel portrait;
            public MultiFormatTextLabel details;
            public Button travelButton;
            public PartyMember boundMember;
            public string portraitKey = string.Empty;
        }

        sealed class PartyTravelRequest
        {
            public string playerName;
            public string locationText;
            public global::PositionMultiplayer.PartyLocationState locationState;
            public int worldX;
            public int worldZ;
            public bool exteriorArrivalYValid;
            public float exteriorArrivalY;
            public DFPosition mapPixel;
            public bool usesSafeBuildingEntranceAnchor;
            public global::PlayerMultiplayer targetPlayer;
            public string dungeonInstanceId;
            public bool useDirectDungeonRendezvous;
        }

        readonly List<PartyMember> members = new List<PartyMember>();
        readonly PartySlot[] slots = new PartySlot[membersPerPage];
        readonly Dictionary<string, Texture2D> portraitCache = new Dictionary<string, Texture2D>();

        Panel mainPanel;
        TextLabel titleLabel;
        TextLabel pageLabel;
        Button dialogNotesButton;
        Button upArrowButton;
        Button downArrowButton;
        Button exitButton;

        int currentPage;
        float nextRefreshTime;
        bool forceRefresh = true;
        PartyTravelRequest pendingTravelRequest;

        public DaggerfallMultiplayerPartyWindow(IUserInterfaceManager uiManager)
            : base(uiManager)
        {
        }

        protected override void Setup()
        {
            base.Setup();

            ParentPanel.BackgroundColor = ScreenDimColor;

            Texture2D journalTexture = DaggerfallUI.GetTextureFromImg(journalBackgroundFilename);
            if (journalTexture == null)
            {
                Debug.LogError("[MultiplayerPartyWindow] Failed to load " + journalBackgroundFilename);
                CloseWindow();
                return;
            }

            mainPanel = DaggerfallUI.AddPanel(NativePanel, AutoSizeModes.None);
            mainPanel.BackgroundTexture = journalTexture;
            mainPanel.Size = new Vector2(320, 200);
            mainPanel.HorizontalAlignment = HorizontalAlignment.Center;
            mainPanel.VerticalAlignment = VerticalAlignment.Middle;
            mainPanel.OnMouseScrollUp += MainPanel_OnMouseScrollUp;
            mainPanel.OnMouseScrollDown += MainPanel_OnMouseScrollDown;

            Panel titlePanel = new Panel();
            titlePanel.Position = new Vector2(30, 21);
            titlePanel.Size = new Vector2(238, 16);

            titleLabel = new TextLabel();
            titleLabel.HorizontalAlignment = HorizontalAlignment.Center;
            titleLabel.Font = DaggerfallUI.LargeFont;
            titleLabel.ShadowColor = new Color(0f, 0.2f, 0.5f);
            titleLabel.Text = "Multiplayer Party";
            titlePanel.Components.Add(titleLabel);
            mainPanel.Components.Add(titlePanel);

            // Four compact, full-width party rows fit between the journal title
            // and the bottom page controls. Full-width rows keep long locations
            // substantially more readable than a two-column 2x2 layout.
            slots[0] = CreateSlot(38f);
            slots[1] = CreateSlot(73f);
            slots[2] = CreateSlot(108f);
            slots[3] = CreateSlot(143f);

            // LGBK00I0.IMG permanently contains the DIALOG NOTES artwork here.
            // Restore the original journal's invisible hitbox and route it to the
            // actual journal Messages (dialog notes) view.
            dialogNotesButton = new Button();
            dialogNotesButton.Position = new Vector2(32, 187);
            dialogNotesButton.Size = new Vector2(68, 10);
            dialogNotesButton.OnMouseClick += DialogNotesButton_OnMouseClick;
            dialogNotesButton.Name = "party_dialog_notes_button";
            mainPanel.Components.Add(dialogNotesButton);

            upArrowButton = new Button();
            upArrowButton.Position = new Vector2(181, 188);
            upArrowButton.Size = new Vector2(13, 7);
            upArrowButton.OnMouseClick += UpArrowButton_OnMouseClick;
            upArrowButton.Name = "party_up_arrow_button";
            mainPanel.Components.Add(upArrowButton);

            downArrowButton = new Button();
            downArrowButton.Position = new Vector2(209, 188);
            downArrowButton.Size = new Vector2(13, 7);
            downArrowButton.OnMouseClick += DownArrowButton_OnMouseClick;
            downArrowButton.Name = "party_down_arrow_button";
            mainPanel.Components.Add(downArrowButton);

            pageLabel = new TextLabel();
            pageLabel.Position = new Vector2(192, 187);
            pageLabel.TextScale = 0.8f;
            pageLabel.HorizontalAlignment = HorizontalAlignment.Center;
            pageLabel.Size = new Vector2(18, 8);
            mainPanel.Components.Add(pageLabel);

            exitButton = new Button();
            exitButton.Position = new Vector2(278, 187);
            exitButton.Size = new Vector2(30, 9);
            exitButton.OnMouseClick += ExitButton_OnMouseClick;
            exitButton.Name = "party_exit_button";
            mainPanel.Components.Add(exitButton);
        }

        PartySlot CreateSlot(float y)
        {
            PartySlot slot = new PartySlot();

            slot.root = new Panel();
            slot.root.Position = new Vector2(29, y);
            slot.root.Size = new Vector2(260, 34);
            slot.root.BackgroundColor = Color.clear;
            mainPanel.Components.Add(slot.root);

            slot.portrait = new Panel();
            slot.portrait.Position = new Vector2(0, 1);
            slot.portrait.Size = new Vector2(31, 31);
            slot.portrait.BackgroundColor = Color.clear;
            slot.portrait.BackgroundTextureLayout = BackgroundLayout.ScaleToFit;
            slot.root.Components.Add(slot.portrait);

            slot.details = new MultiFormatTextLabel();
            slot.details.Position = new Vector2(36, 0);
            slot.details.Size = new Vector2(224, 34);
            slot.details.TextScale = 0.72f;
            slot.details.HighlightColor = Color.white;
            slot.details.WrapText = true;
            slot.details.WrapWords = true;
            slot.details.MaxTextWidth = 224;
            slot.root.Components.Add(slot.details);

            // Transparent click target over the third text line only (the Location line).
            // At TextScale 0.72 the third baseline starts around local Y=12. The previous
            // Y=20 hitbox sat below the rendered location text, so clicking the white line
            // did nothing even though travel was enabled.
            slot.travelButton = new Button();
            slot.travelButton.Position = new Vector2(34, 11);
            slot.travelButton.Size = new Vector2(226, 13);
            slot.travelButton.Name = "party_travel_location_button";
            slot.travelButton.OnMouseClick += (sender, position) => TravelButton_OnMouseClick(slot);
            slot.root.Components.Add(slot.travelButton);

            return slot;
        }

        public override void OnPush()
        {
            base.OnPush();
            currentPage = 0;
            forceRefresh = true;
            nextRefreshTime = 0f;
            DaggerfallUI.Instance.PlayOneShot(SoundClips.OpenBook);
        }

        public override void Update()
        {
            base.Update();

            if (forceRefresh || Time.realtimeSinceStartup >= nextRefreshTime)
            {
                forceRefresh = false;
                nextRefreshTime = Time.realtimeSinceStartup + refreshInterval;
                RefreshMembers();
                RefreshPage();
            }
        }

        void RefreshMembers()
        {
            members.Clear();

            global::PlayerMultiplayer[] players = UnityEngine.Object.FindObjectsOfType<global::PlayerMultiplayer>();
            for (int i = 0; i < players.Length; i++)
            {
                global::PlayerMultiplayer player = players[i];
                if (player == null || !player.isActiveAndEnabled)
                    continue;

                global::PlayerAssets assets = player.GetComponent<global::PlayerAssets>();
                if (assets == null || string.IsNullOrEmpty(assets.playerName))
                    continue;

                if (string.Equals(assets.playerName, "Nameless", StringComparison.OrdinalIgnoreCase))
                    continue;

                PartyMember member = new PartyMember();
                member.key = player.netId != 0 ? player.netId : unchecked((uint)player.GetInstanceID());
                member.player = player;
                member.assets = assets;
                member.position = player.GetComponent<global::PositionMultiplayer>();
                member.isLocalPlayer = player.isLocalPlayer;
                members.Add(member);
            }

            members.Sort(CompareMembers);

            int pageCount = GetPageCount();
            if (currentPage >= pageCount)
                currentPage = Mathf.Max(0, pageCount - 1);
        }

        int CompareMembers(PartyMember a, PartyMember b)
        {
            if (a.isLocalPlayer != b.isLocalPlayer)
                return a.isLocalPlayer ? -1 : 1;

            string aName = a.assets != null ? a.assets.playerName : string.Empty;
            string bName = b.assets != null ? b.assets.playerName : string.Empty;
            int nameCompare = string.Compare(aName, bName, StringComparison.OrdinalIgnoreCase);
            if (nameCompare != 0)
                return nameCompare;

            return a.key.CompareTo(b.key);
        }

        int GetPageCount()
        {
            return Mathf.Max(1, Mathf.CeilToInt(members.Count / (float)membersPerPage));
        }

        void RefreshPage()
        {
            int pageCount = GetPageCount();
            pageLabel.Text = string.Format("{0}/{1}", currentPage + 1, pageCount);
            titleLabel.Text = string.Format("Multiplayer Party ({0})", members.Count);

            upArrowButton.Enabled = currentPage > 0;
            downArrowButton.Enabled = currentPage + 1 < pageCount;

            for (int slotIndex = 0; slotIndex < slots.Length; slotIndex++)
            {
                int memberIndex = currentPage * membersPerPage + slotIndex;
                PartySlot slot = slots[slotIndex];

                if (memberIndex < 0 || memberIndex >= members.Count)
                {
                    slot.root.Enabled = false;
                    slot.details.Clear();
                    slot.portrait.BackgroundTexture = null;
                    slot.portraitKey = string.Empty;
                    slot.boundMember = null;
                    slot.travelButton.Enabled = false;
                    continue;
                }

                slot.root.Enabled = true;
                UpdateSlot(slot, members[memberIndex]);
            }

            if (members.Count == 0)
            {
                slots[0].root.Enabled = true;
                slots[0].portrait.BackgroundTexture = null;
                slots[0].portraitKey = string.Empty;
                slots[0].boundMember = null;
                slots[0].travelButton.Enabled = false;
                slots[0].details.Clear();
                slots[0].details.SetText(new TextFile.Token[]
                {
                    TextFile.CreateTextToken("No connected party members are available."),
                    TextFile.CreateFormatToken(TextFile.Formatting.EndOfRecord),
                });
            }
        }

        void UpdateSlot(PartySlot slot, PartyMember member)
        {
            slot.boundMember = member;
            bool canTravel = CanTravelToMember(member);
            slot.travelButton.Enabled = canTravel;

            UpdatePortrait(slot, member.assets);

            string name = member.assets.playerName ?? "Unknown";
            string job = string.IsNullOrEmpty(member.assets.job) ? "Unknown" : member.assets.job;
            global::PlayerMultiplayer player = member.player;

            List<TextFile.Token> tokens = new List<TextFile.Token>();

            string nameLine = member.isLocalPlayer ? name + " (You)" : name;
            tokens.Add(new TextFile.Token() { text = nameLine, formatting = TextFile.Formatting.TextHighlight });
            tokens.Add(TextFile.CreateTextToken(string.Format(
                "  {0}  Lv {1}",
                job,
                Mathf.Max(1, player.PlayerMPLevel))));
            tokens.Add(TextFile.NewLineToken);

            // DFU stores fatigue in fixed-point units multiplied by 64. The normal
            // character sheet divides both values by FatigueMultiplier before display.
            // Keep the synchronized raw values unchanged because the compact bar ratio
            // already depends on raw current/raw maximum and is therefore correct.
            int displayedCurrentFatigue = ToDisplayedFatigue(player.PlayerMPCurrentFatigue);
            int displayedMaxFatigue = Mathf.Max(1, ToDisplayedFatigue(player.PlayerMPMaxFatigue));

            tokens.Add(TextFile.CreateTextToken(string.Format(
                "HP {0}/{1}   MP {2}/{3}   FAT {4}/{5}",
                Mathf.Max(0, player.PlayerMPCurrentHealth),
                Mathf.Max(1, player.PlayerMPMaxHealth),
                Mathf.Max(0, player.PlayerMPCurrentMagicka),
                Mathf.Max(0, player.PlayerMPMaxMagicka),
                displayedCurrentFatigue,
                displayedMaxFatigue)));
            tokens.Add(TextFile.NewLineToken);

            TextFile.Token locationToken = TextFile.CreateTextToken("Location: " + GetFullLocation(member.position));
            if (canTravel)
                locationToken.formatting = TextFile.Formatting.TextHighlight;
            tokens.Add(locationToken);
            tokens.Add(TextFile.CreateFormatToken(TextFile.Formatting.EndOfRecord));

            slot.details.Clear();
            slot.details.SetText(tokens.ToArray());
        }

        bool CanTravelToMember(PartyMember member)
        {
            int worldX, worldZ;
            bool exteriorArrivalYValid;
            float exteriorArrivalY;
            DFPosition mapPixel;
            bool usesSafeBuildingEntranceAnchor;
            bool useDirectDungeonRendezvous;
            return TryResolveTravelDestination(
                member,
                out worldX,
                out worldZ,
                out exteriorArrivalYValid,
                out exteriorArrivalY,
                out mapPixel,
                out usesSafeBuildingEntranceAnchor,
                out useDirectDungeonRendezvous);
        }

        bool TryResolveTravelDestination(
            PartyMember member,
            out int worldX,
            out int worldZ,
            out bool exteriorArrivalYValid,
            out float exteriorArrivalY,
            out DFPosition mapPixel,
            out bool usesSafeBuildingEntranceAnchor,
            out bool useDirectDungeonRendezvous)
        {
            worldX = 0;
            worldZ = 0;
            exteriorArrivalYValid = false;
            exteriorArrivalY = 0f;
            mapPixel = new DFPosition();
            usesSafeBuildingEntranceAnchor = false;
            useDirectDungeonRendezvous = false;

            if (member == null || member.isLocalPlayer || member.position == null)
                return false;

            global::PositionMultiplayer position = member.position;
            if (!position.PartyLocationShared ||
                position.PartyCurrentLocationState == global::PositionMultiplayer.PartyLocationState.Unknown)
                return false;

            // Dungeon rendezvous requires the stable identity of the exact generated
            // network dungeon. The target's live interior XYZ already comes from its
            // existing NetworkTransform and is intentionally not duplicated here.
            if (position.PartyCurrentLocationState == global::PositionMultiplayer.PartyLocationState.DungeonInterior &&
                string.IsNullOrEmpty(position.PartyDungeonInstanceId))
                return false;

            if (position.PartyCurrentLocationState == global::PositionMultiplayer.PartyLocationState.BuildingInterior)
            {
                // Interior PlayerGPS x/z tracks movement inside the interior and can overlap
                // the exterior building footprint. Require the dedicated safe doorway anchor
                // calculated from PlayerEnterExit's normal building-exit data.
                if (!position.PartyBuildingEntranceAnchorValid)
                    return false;

                worldX = position.PartyBuildingEntranceAnchorX;
                worldZ = position.PartyBuildingEntranceAnchorZ;
                usesSafeBuildingEntranceAnchor = true;
            }
            else
            {
                worldX = position.x;
                worldZ = position.z;

                // Only exterior/wilderness players publish a meaningful shared Unity Y.
                // Raised town platforms, castle stairs, exterior dungeon structures, and
                // rooftops therefore preserve the target player's actual elevation.
                exteriorArrivalYValid = position.PartyExteriorArrivalYValid;
                exteriorArrivalY = position.PartyExteriorArrivalY;
            }

            if (TryGetMapPixel(worldX, worldZ, out mapPixel))
                return true;

            // Some quest/teleport-only dungeons deliberately live outside DFU's
            // streamable exterior map (Mantellan Crux is map pixel 1,1). A party
            // member already inside one of these dungeons still provides a valid,
            // exact live network-dungeon instance ID. Allow that target and let the
            // travel popup skip exterior construction before using its existing
            // instance-validated dungeon rendezvous.
            //
            // This is intentionally generic: no dungeon name or map ID is hardcoded.
            // Non-dungeon targets with invalid coordinates remain unavailable.
            if (position.PartyCurrentLocationState ==
                global::PositionMultiplayer.PartyLocationState.DungeonInterior)
            {
                useDirectDungeonRendezvous = true;
                return true;
            }

            return false;
        }

        static bool TryGetMapPixel(int worldX, int worldZ, out DFPosition mapPixel)
        {
            mapPixel = new DFPosition();

            // Zero coordinates are used while a PlayerMultiplayer has not published a real GPS
            // position yet. Never turn that transient state into a trip to the edge of the map.
            if (worldX <= 0 || worldZ <= 0)
                return false;

            mapPixel = MapsFile.WorldCoordToMapPixel(worldX, worldZ);
            return mapPixel.X >= TerrainHelper.minMapPixelX &&
                   mapPixel.X <= TerrainHelper.maxMapPixelX &&
                   mapPixel.Y >= TerrainHelper.minMapPixelY &&
                   mapPixel.Y <= TerrainHelper.maxMapPixelY;
        }

        void TravelButton_OnMouseClick(PartySlot slot)
        {
            PartyMember member = slot != null ? slot.boundMember : null;
            if (!CanTravelToMember(member))
                return;

            int targetWorldX, targetWorldZ;
            bool exteriorArrivalYValid;
            float exteriorArrivalY;
            DFPosition targetMapPixel;
            bool usesSafeBuildingEntranceAnchor;
            bool useDirectDungeonRendezvous;
            if (!TryResolveTravelDestination(
                member,
                out targetWorldX,
                out targetWorldZ,
                out exteriorArrivalYValid,
                out exteriorArrivalY,
                out targetMapPixel,
                out usesSafeBuildingEntranceAnchor,
                out useDirectDungeonRendezvous))
                return;

            DaggerfallUI.Instance.PlayOneShot(SoundClips.ButtonClick);

            pendingTravelRequest = new PartyTravelRequest()
            {
                playerName = member.assets != null && !string.IsNullOrEmpty(member.assets.playerName)
                    ? member.assets.playerName
                    : "party member",
                locationText = GetFullLocation(member.position),
                locationState = member.position.PartyCurrentLocationState,
                worldX = targetWorldX,
                worldZ = targetWorldZ,
                exteriorArrivalYValid = exteriorArrivalYValid,
                exteriorArrivalY = exteriorArrivalY,
                mapPixel = targetMapPixel,
                usesSafeBuildingEntranceAnchor = usesSafeBuildingEntranceAnchor,
                targetPlayer = member.player,
                dungeonInstanceId = member.position.PartyDungeonInstanceId,
                useDirectDungeonRendezvous = useDirectDungeonRendezvous,
            };

            ShowPartyTravelConfirmation(pendingTravelRequest);
        }

        void ShowPartyTravelConfirmation(PartyTravelRequest request)
        {
            if (request == null)
                return;

            const int doYouWishToTravelToTextId = 31;
            TextFile.Token[] classicTokens = DaggerfallUnity.Instance.TextProvider.GetRSCTokens(doYouWishToTravelToTextId);
            List<TextFile.Token> tokens = new List<TextFile.Token>();
            bool replacedDestination = false;

            if (classicTokens != null)
            {
                for (int i = 0; i < classicTokens.Length; i++)
                {
                    TextFile.Token token = classicTokens[i];
                    if (token.formatting == TextFile.Formatting.EndOfRecord)
                        continue;

                    if (!string.IsNullOrEmpty(token.text) && token.text.Contains("%tcn"))
                    {
                        token.text = token.text.Replace(
                            "%tcn",
                            request.playerName + " at " + request.locationText);
                        replacedDestination = true;
                    }

                    tokens.Add(token);
                }
            }

            if (!replacedDestination)
                tokens.Add(TextFile.CreateTextToken("Do you wish to travel to " + request.playerName + "?"));

            tokens.Add(TextFile.NewLineToken);
            if (request.useDirectDungeonRendezvous)
            {
                tokens.Add(TextFile.CreateTextToken(
                    "Destination: " + request.locationText + "  (direct dungeon rendezvous)"));
            }
            else
            {
                tokens.Add(TextFile.CreateTextToken(string.Format(
                    "Destination: {0}  ({1}, {2})",
                    request.locationText,
                    request.mapPixel.X,
                    request.mapPixel.Y)));
            }

            if (request.locationState == global::PositionMultiplayer.PartyLocationState.BuildingInterior)
            {
                tokens.Add(TextFile.NewLineToken);
                tokens.Add(TextFile.CreateTextToken(
                    request.usesSafeBuildingEntranceAnchor
                        ? "Arrival will be outside this building's entrance."
                        : "The building entrance anchor is not available yet."));
            }
            else if (request.locationState == global::PositionMultiplayer.PartyLocationState.DungeonInterior)
            {
                tokens.Add(TextFile.NewLineToken);
                tokens.Add(TextFile.CreateTextToken(
                    request.useDirectDungeonRendezvous
                        ? "This dungeon has no streamable exterior. Arrival will be directly inside near this player."
                        : "Arrival will be inside the dungeon near this player."));
            }

            tokens.Add(TextFile.CreateFormatToken(TextFile.Formatting.EndOfRecord));

            DaggerfallMessageBox messageBox = new DaggerfallMessageBox(uiManager, this);
            messageBox.SetTextTokens(tokens.ToArray());
            messageBox.AddButton(DaggerfallMessageBox.MessageBoxButtons.Yes);
            messageBox.AddButton(DaggerfallMessageBox.MessageBoxButtons.No);
            messageBox.OnButtonClick += ConfirmPartyTravelButtonClick;
            uiManager.PushWindow(messageBox);
        }

        void ConfirmPartyTravelButtonClick(
            DaggerfallMessageBox sender,
            DaggerfallMessageBox.MessageBoxButtons messageBoxButton)
        {
            DaggerfallUI.Instance.PlayOneShot(SoundClips.ButtonClick);
            sender.CloseWindow();

            PartyTravelRequest request = pendingTravelRequest;
            pendingTravelRequest = null;

            if (messageBoxButton != DaggerfallMessageBox.MessageBoxButtons.Yes || request == null)
                return;

            OpenPartyTravelPopup(request);
        }

        void OpenPartyTravelPopup(PartyTravelRequest request)
        {
            if (request == null || GameManager.Instance == null)
                return;

            DFPosition startMapPixel = GetLocalPartyTravelStartPixel();

            DaggerfallTravelPopUp travelPopup =
                (DaggerfallTravelPopUp)UIWindowFactory.GetInstanceWithArgs(
                    UIWindowType.TravelPopUp,
                    new object[] { uiManager, this, null });

            if (travelPopup == null)
            {
                Debug.LogWarning("[MultiplayerPartyWindow] Could not create the normal travel popup.");
                return;
            }

            // Direct off-map rendezvous has no valid exterior destination. Reuse the
            // start pixel only for the popup's time/cost calculation; the popup will
            // not construct or teleport to that exterior before entering the dungeon.
            travelPopup.EndPos = request.useDirectDungeonRendezvous
                ? startMapPixel
                : request.mapPixel;
            // Exterior players use their current synced x/z, network dungeons use the
            // stable dungeon entrance anchor, and building interiors now use the dedicated
            // safe exterior doorway anchor published by PositionMultiplayer.
            bool useDungeonEntranceReposition =
                request.locationState == global::PositionMultiplayer.PartyLocationState.DungeonInterior &&
                !request.useDirectDungeonRendezvous;

            // Exterior players retain exact X/Z/Y rendezvous placement. Dungeon targets use
            // DFU's normal dungeon entrance reposition so raised stairs/platforms get their
            // actual door height after the exterior location has finished streaming.
            bool useExactDestination =
                !request.useDirectDungeonRendezvous &&
                !useDungeonEntranceReposition &&
                (request.locationState != global::PositionMultiplayer.PartyLocationState.BuildingInterior ||
                 request.usesSafeBuildingEntranceAnchor);

            travelPopup.ConfigurePartyTravel(
                startMapPixel,
                request.worldX,
                request.worldZ,
                useExactDestinationWorldCoordinates: useExactDestination,
                closeSourceWindowAfterTravel: true,
                useExactDestinationWorldY: request.exteriorArrivalYValid,
                destinationWorldY: request.exteriorArrivalY,
                useDungeonEntranceReposition: useDungeonEntranceReposition,
                usePartyDungeonRendezvous:
                    request.locationState == global::PositionMultiplayer.PartyLocationState.DungeonInterior,
                partyDungeonTargetPlayer: request.targetPlayer,
                partyDungeonInstanceId: request.dungeonInstanceId,
                useDirectPartyDungeonRendezvous: request.useDirectDungeonRendezvous);
            uiManager.PushWindow(travelPopup);
        }

        DFPosition GetLocalPartyTravelStartPixel()
        {
            for (int i = 0; i < members.Count; i++)
            {
                PartyMember member = members[i];
                if (member == null || !member.isLocalPlayer || member.position == null)
                    continue;

                // This immediately refreshes x/z from PlayerGPS, or from the stable dungeon
                // entrance anchor while inside a network dungeon.
                member.position.ForceSendCurrentCoordinatesNow("party-fast-travel-start");

                DFPosition mapPixel;
                if (TryGetMapPixel(member.position.x, member.position.z, out mapPixel))
                    return mapPixel;
            }

            return TravelTimeCalculator.GetPlayerTravelPosition();
        }

        static int ToDisplayedFatigue(int rawFatigue)
        {
            int multiplier = Mathf.Max(1, DaggerfallEntity.FatigueMultiplier);
            return Mathf.Max(0, rawFatigue) / multiplier;
        }

        void UpdatePortrait(PartySlot slot, global::PlayerAssets assets)
        {
            string archive = assets != null ? assets.faceArchive ?? string.Empty : string.Empty;
            int faceIndex = assets != null ? Mathf.Clamp(assets.faceIndex, 0, 9) : 0;
            string key = archive + ":" + faceIndex;

            if (string.Equals(slot.portraitKey, key, StringComparison.Ordinal))
                return;

            slot.portraitKey = key;
            slot.portrait.BackgroundTexture = null;

            if (string.IsNullOrEmpty(archive))
                return;

            Texture2D texture;
            if (portraitCache.TryGetValue(key, out texture))
            {
                slot.portrait.BackgroundTexture = texture;
                return;
            }

            try
            {
                ImageData imageData = ImageReader.GetImageData(archive, faceIndex, 0, true, true);
                texture = imageData.texture;
                if (texture != null)
                {
                    portraitCache[key] = texture;
                    slot.portrait.BackgroundTexture = texture;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MultiplayerPartyWindow] Could not load portrait " + key + ": " + ex.Message);
            }
        }

        string GetFullLocation(global::PositionMultiplayer position)
        {
            if (position == null || !position.PartyLocationShared)
                return "Hidden";

            string region = position.PartyRegionName ?? string.Empty;
            string location = position.PartyLocationName ?? string.Empty;
            string result;

            switch (position.PartyCurrentLocationState)
            {
                case global::PositionMultiplayer.PartyLocationState.DungeonInterior:
                    result = !string.IsNullOrEmpty(location) ? "Dungeon - " + location : "Inside a dungeon";
                    break;

                case global::PositionMultiplayer.PartyLocationState.BuildingInterior:
                    result = !string.IsNullOrEmpty(location) ? "Building interior - " + location : "Inside a building";
                    break;

                case global::PositionMultiplayer.PartyLocationState.ExteriorLocation:
                    result = !string.IsNullOrEmpty(location) ? location : "Named location";
                    break;

                case global::PositionMultiplayer.PartyLocationState.Wilderness:
                    result = "Wilderness";
                    break;

                default:
                    result = !string.IsNullOrEmpty(location) ? location : "Unknown";
                    break;
            }

            if (!string.IsNullOrEmpty(region))
                result += ", " + region;

            return result;
        }

        void PreviousPage()
        {
            if (currentPage <= 0)
                return;

            currentPage--;
            forceRefresh = true;
            DaggerfallUI.Instance.PlayOneShot(SoundClips.PageTurn);
        }

        void NextPage()
        {
            if (currentPage + 1 >= GetPageCount())
                return;

            currentPage++;
            forceRefresh = true;
            DaggerfallUI.Instance.PlayOneShot(SoundClips.PageTurn);
        }

        void MainPanel_OnMouseScrollUp(BaseScreenComponent sender)
        {
            PreviousPage();
        }

        void MainPanel_OnMouseScrollDown(BaseScreenComponent sender)
        {
            NextPage();
        }

        void DialogNotesButton_OnMouseClick(BaseScreenComponent sender, Vector2 position)
        {
            // This artwork belongs to the original quest journal. Switch from the
            // party roster to that journal's real Messages/Dialog Notes category.
            DaggerfallQuestJournalWindow journalWindow = new DaggerfallQuestJournalWindow(uiManager);
            journalWindow.DisplayMode = DaggerfallQuestJournalWindow.JournalDisplay.Messages;

            CloseWindow();
            uiManager.PushWindow(journalWindow);
        }

        void UpArrowButton_OnMouseClick(BaseScreenComponent sender, Vector2 position)
        {
            PreviousPage();
        }

        void DownArrowButton_OnMouseClick(BaseScreenComponent sender, Vector2 position)
        {
            NextPage();
        }

        void ExitButton_OnMouseClick(BaseScreenComponent sender, Vector2 position)
        {
            DaggerfallUI.Instance.PlayOneShot(SoundClips.ButtonClick);
            CloseWindow();
        }
    }
}
