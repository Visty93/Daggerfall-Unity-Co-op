// Project:         Daggerfall Unity - TanguyMultiplayer party HUD
// Notes:           Compact, display-only remote party cards.

using System;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop.Utility;

namespace DaggerfallWorkshop.Game.UserInterface
{
    /// <summary>
    /// Displays one compact status card for every remote PlayerMultiplayer object.
    /// The local player is deliberately omitted. In singleplayer this panel remains empty.
    /// </summary>
    public class HUDMultiplayerParty : Panel
    {
        const string healthBarFilename = "MAIN03I0.IMG";
        const string fatigueBarFilename = "MAIN04I0.IMG";
        const string magickaBarFilename = "MAIN05I0.IMG";

        // Aspect-preserving 640x400 HUD-space layout. DaggerfallHUD aligns this
        // panel to the actual top-right edge without stretching portraits on widescreen.
        // Cards deliberately use a narrow step so remote players sit close together.
        const float cardWidth = 60f;
        const float cardHeight = 52f;
        const float cardGap = 0.5f;
        const float rowGap = 0.5f;
        const float rightMargin = 2f;
        const float topMargin = 2f;

        const float portraitWidth = 28f;
        const float portraitHeight = 25f;
        const float barWidth = 3.5f;
        const float barHeight = 25f;
        const float barGap = 1f;
        const float textScale = 0.92f;

        const float rosterRefreshInterval = 0.50f;

        sealed class PartyEntry
        {
            public uint key;
            public global::PlayerMultiplayer player;
            public global::PlayerAssets assets;
            public global::PositionMultiplayer position;

            public Panel root;
            public Panel portrait;
            public VerticalProgress healthBar;
            public VerticalProgress fatigueBar;
            public VerticalProgress magickaBar;
            public TextLabel nameLevelLabel;
            public TextLabel hpLabel;
            public TextLabel locationLabel;

            public string portraitKey = string.Empty;
        }

        readonly Dictionary<uint, PartyEntry> entries = new Dictionary<uint, PartyEntry>();
        readonly List<PartyEntry> visibleEntries = new List<PartyEntry>();
        readonly List<uint> removeKeys = new List<uint>();
        readonly HashSet<uint> seenKeys = new HashSet<uint>();

        float nextRosterRefreshTime;
        Texture2D healthTexture;
        Texture2D fatigueTexture;
        Texture2D magickaTexture;

        public HUDMultiplayerParty()
            : base()
        {
            BackgroundColor = Color.clear;
            LoadVitalTextures();
        }

        public override void Update()
        {
            if (!Enabled)
                return;

            if (!IsMultiplayerActive())
            {
                ClearEntries();
                base.Update();
                return;
            }

            if (Time.realtimeSinceStartup >= nextRosterRefreshTime)
            {
                nextRosterRefreshTime = Time.realtimeSinceStartup + rosterRefreshInterval;
                RefreshRoster();
            }

            UpdateEntryContents();
            LayoutVisibleEntries();
            base.Update();
        }

        void LoadVitalTextures()
        {
            bool swapHealthAndFatigue = DaggerfallUnity.Settings.SwapHealthAndFatigueColors;
            healthTexture = DaggerfallUI.GetTextureFromImg(swapHealthAndFatigue ? fatigueBarFilename : healthBarFilename);
            fatigueTexture = DaggerfallUI.GetTextureFromImg(swapHealthAndFatigue ? healthBarFilename : fatigueBarFilename);
            magickaTexture = DaggerfallUI.GetTextureFromImg(magickaBarFilename);
        }

        bool IsMultiplayerActive()
        {
            if (NetworkClient.active || NetworkServer.active)
                return true;

            try
            {
                return global::PlayerMultiplayer.state != 0;
            }
            catch
            {
                return false;
            }
        }

        void RefreshRoster()
        {
            seenKeys.Clear();

            global::PlayerMultiplayer[] players = UnityEngine.Object.FindObjectsOfType<global::PlayerMultiplayer>();
            for (int i = 0; i < players.Length; i++)
            {
                global::PlayerMultiplayer player = players[i];
                if (player == null || player.isLocalPlayer || !player.isActiveAndEnabled)
                    continue;

                uint key = GetStableEntryKey(player);
                if (!seenKeys.Add(key))
                    continue;

                PartyEntry entry;
                if (!entries.TryGetValue(key, out entry))
                {
                    entry = CreateEntry(key, player);
                    entries.Add(key, entry);
                }
                else
                {
                    entry.player = player;
                    entry.assets = player.GetComponent<global::PlayerAssets>();
                    entry.position = player.GetComponent<global::PositionMultiplayer>();
                }
            }

            removeKeys.Clear();
            foreach (KeyValuePair<uint, PartyEntry> pair in entries)
            {
                if (!seenKeys.Contains(pair.Key))
                    removeKeys.Add(pair.Key);
            }

            for (int i = 0; i < removeKeys.Count; i++)
                RemoveEntry(removeKeys[i]);
        }

        uint GetStableEntryKey(global::PlayerMultiplayer player)
        {
            if (player.netId != 0)
                return player.netId;

            // Defensive fallback for the very short pre-spawn window.
            return unchecked((uint)player.GetInstanceID());
        }

        PartyEntry CreateEntry(uint key, global::PlayerMultiplayer player)
        {
            PartyEntry entry = new PartyEntry();
            entry.key = key;
            entry.player = player;
            entry.assets = player.GetComponent<global::PlayerAssets>();
            entry.position = player.GetComponent<global::PositionMultiplayer>();

            entry.root = new Panel();
            entry.root.Size = new Vector2(cardWidth, cardHeight);
            entry.root.BackgroundColor = Color.clear;
            entry.root.Enabled = false;

            entry.portrait = new Panel();
            entry.portrait.Position = Vector2.zero;
            entry.portrait.Size = new Vector2(portraitWidth, portraitHeight);
            entry.portrait.BackgroundColor = Color.clear;
            // Preserve the source portrait aspect ratio. StretchToFill distorts faces
            // whenever the texture and panel dimensions differ slightly.
            entry.portrait.BackgroundTextureLayout = BackgroundLayout.ScaleToFit;
            entry.root.Components.Add(entry.portrait);

            float firstBarX = entry.portrait.Position.x + portraitWidth + 2f;
            entry.healthBar = CreateVitalBar(healthTexture, firstBarX);
            entry.fatigueBar = CreateVitalBar(fatigueTexture, firstBarX + barWidth + barGap);
            entry.magickaBar = CreateVitalBar(magickaTexture, firstBarX + (barWidth + barGap) * 2f);
            entry.root.Components.Add(entry.healthBar);
            entry.root.Components.Add(entry.fatigueBar);
            entry.root.Components.Add(entry.magickaBar);

            entry.nameLevelLabel = CreateLabel(new Vector2(0f, 28f));
            entry.hpLabel = CreateLabel(new Vector2(0f, 36f));
            entry.locationLabel = CreateLabel(new Vector2(0f, 44f));
            entry.locationLabel.TextColor = new Color(0.82f, 0.82f, 0.82f, 1f);

            entry.root.Components.Add(entry.nameLevelLabel);
            entry.root.Components.Add(entry.hpLabel);
            entry.root.Components.Add(entry.locationLabel);

            Components.Add(entry.root);
            return entry;
        }

        VerticalProgress CreateVitalBar(Texture2D texture, float x)
        {
            VerticalProgress bar = new VerticalProgress(texture);
            bar.Position = new Vector2(x, 0f);
            bar.Size = new Vector2(barWidth, barHeight);
            bar.Amount = 0f;
            return bar;
        }

        TextLabel CreateLabel(Vector2 position)
        {
            TextLabel label = new TextLabel();
            label.Position = position;
            label.TextScale = textScale;
            label.ShadowPosition = new Vector2(1f, 1f);
            label.MaxCharacters = 17;
            return label;
        }

        void UpdateEntryContents()
        {
            visibleEntries.Clear();

            foreach (KeyValuePair<uint, PartyEntry> pair in entries)
            {
                PartyEntry entry = pair.Value;
                if (!IsEntryReady(entry))
                {
                    entry.root.Enabled = false;
                    continue;
                }

                entry.root.Enabled = true;
                visibleEntries.Add(entry);

                global::PlayerMultiplayer player = entry.player;
                global::PlayerAssets assets = entry.assets;

                entry.healthBar.Amount = player.PlayerMPHealthPercent;
                entry.fatigueBar.Amount = player.PlayerMPFatiguePercent;
                entry.magickaBar.Amount = player.PlayerMPMagickaPercent;

                string displayName = assets.playerName ?? string.Empty;
                entry.nameLevelLabel.Text = Shorten(
                    string.Format("{0}, Lv{1}", displayName, Mathf.Max(1, player.PlayerMPLevel)),
                    16);

                entry.hpLabel.Text = string.Format(
                    "HP: {0}/{1}",
                    Mathf.Max(0, player.PlayerMPCurrentHealth),
                    Mathf.Max(1, player.PlayerMPMaxHealth));

                entry.locationLabel.Text = Shorten(GetCompactLocation(entry.position), 16);
                UpdatePortrait(entry);
            }

            visibleEntries.Sort(CompareEntries);
        }

        bool IsEntryReady(PartyEntry entry)
        {
            if (entry == null || entry.player == null || entry.assets == null)
                return false;

            string playerName = entry.assets.playerName;
            if (string.IsNullOrEmpty(playerName))
                return false;

            if (string.Equals(playerName, "Nameless", StringComparison.OrdinalIgnoreCase))
                return false;

            return entry.player.PlayerMPMaxHealth > 0;
        }

        int CompareEntries(PartyEntry a, PartyEntry b)
        {
            string aName = a.assets != null ? a.assets.playerName : string.Empty;
            string bName = b.assets != null ? b.assets.playerName : string.Empty;
            int nameCompare = string.Compare(aName, bName, StringComparison.OrdinalIgnoreCase);
            if (nameCompare != 0)
                return nameCompare;

            return a.key.CompareTo(b.key);
        }

        void UpdatePortrait(PartyEntry entry)
        {
            string archive = entry.assets.faceArchive ?? string.Empty;
            int faceIndex = Mathf.Clamp(entry.assets.faceIndex, 0, 9);
            string newPortraitKey = archive + ":" + faceIndex;

            if (string.Equals(entry.portraitKey, newPortraitKey, StringComparison.Ordinal))
                return;

            entry.portraitKey = newPortraitKey;
            entry.portrait.BackgroundTexture = null;

            if (string.IsNullOrEmpty(archive))
                return;

            try
            {
                ImageData imageData = ImageReader.GetImageData(archive, faceIndex, 0, true, true);
                if (imageData.texture != null)
                    entry.portrait.BackgroundTexture = imageData.texture;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[HUDMultiplayerParty] Could not load portrait " + newPortraitKey +
                    " for " + (entry.assets.playerName ?? "remote player") + ": " + ex.Message);
            }
        }

        string GetCompactLocation(global::PositionMultiplayer position)
        {
            if (position == null || !position.PartyLocationShared)
                return "Location hidden";

            string region = position.PartyRegionName ?? string.Empty;
            string location = position.PartyLocationName ?? string.Empty;

            switch (position.PartyCurrentLocationState)
            {
                case global::PositionMultiplayer.PartyLocationState.DungeonInterior:
                    return !string.IsNullOrEmpty(location) ? "Dungeon: " + location : "Inside dungeon";

                case global::PositionMultiplayer.PartyLocationState.BuildingInterior:
                    return !string.IsNullOrEmpty(location) ? "Inside: " + location : "Inside building";

                case global::PositionMultiplayer.PartyLocationState.ExteriorLocation:
                    if (!string.IsNullOrEmpty(location))
                        return location;
                    if (!string.IsNullOrEmpty(region))
                        return region;
                    return "Location unknown";

                case global::PositionMultiplayer.PartyLocationState.Wilderness:
                    return !string.IsNullOrEmpty(region) ? region + " wilderness" : "Wilderness";

                default:
                    if (!string.IsNullOrEmpty(location))
                        return location;
                    if (!string.IsNullOrEmpty(region))
                        return region;
                    return "Location unknown";
            }
        }

        string Shorten(string text, int maxCharacters)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxCharacters)
                return text ?? string.Empty;

            if (maxCharacters <= 3)
                return text.Substring(0, maxCharacters);

            return text.Substring(0, maxCharacters - 3) + "...";
        }

        void LayoutVisibleEntries()
        {
            float panelWidth = Size.x > 0 ? Size.x : 640f;
            int cardsPerRow = Mathf.Max(
                1,
                Mathf.FloorToInt((panelWidth - rightMargin * 2f + cardGap) / (cardWidth + cardGap)));

            for (int i = 0; i < visibleEntries.Count; i++)
            {
                int row = i / cardsPerRow;
                int column = i % cardsPerRow;

                float x = panelWidth - rightMargin - cardWidth - column * (cardWidth + cardGap);
                float y = topMargin + row * (cardHeight + rowGap);
                visibleEntries[i].root.Position = new Vector2(x, y);
            }
        }

        void RemoveEntry(uint key)
        {
            PartyEntry entry;
            if (!entries.TryGetValue(key, out entry))
                return;

            if (entry.root != null)
                Components.Remove(entry.root);

            entries.Remove(key);
        }

        void ClearEntries()
        {
            if (entries.Count == 0)
                return;

            removeKeys.Clear();
            foreach (uint key in entries.Keys)
                removeKeys.Add(key);

            for (int i = 0; i < removeKeys.Count; i++)
                RemoveEntry(removeKeys[i]);

            visibleEntries.Clear();
        }
    }
}
