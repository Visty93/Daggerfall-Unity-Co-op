using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class OptionsMultiplayer : MonoBehaviour
{

	public static bool timeHost = true;
	public static bool displayName = true;
	public static bool useHighestLevel = false;
	public static bool sendLocation = true;
	public static bool sendMessage = true;
	public static bool mobileNpcSync = true;
    public static int reviveHoldSeconds = 5;
    public static int manualRespawnSeconds = 10;
    public static int reviveHealthPercent = 30;
    public static int respawnHealthPercent = 30;
    public static bool respawnOutsideDungeon = false;
    public static bool partyTravelInsideDungeon = true;

    const string RespawnPrefsPrefix = "DFUCoop.RespawnTravel.";
    static bool localRespawnSettingsLoaded;
    static bool usingRemoteRespawnSettings;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void InitializeSavedRespawnSettings()
    {
        localRespawnSettingsLoaded = false;
        usingRemoteRespawnSettings = false;
        LoadLocalRespawnSettings();
    }

    static void LoadLocalRespawnSettings()
    {
        // Missing keys mean first use: apply the original defaults independently per setting.
        reviveHoldSeconds = PlayerPrefs.GetInt(RespawnPrefsPrefix + "ReviveHoldSeconds", 5);
        manualRespawnSeconds = PlayerPrefs.GetInt(RespawnPrefsPrefix + "ManualRespawnSeconds", 10);
        reviveHealthPercent = PlayerPrefs.GetInt(RespawnPrefsPrefix + "ReviveHealthPercent", 30);
        respawnHealthPercent = PlayerPrefs.GetInt(RespawnPrefsPrefix + "RespawnHealthPercent", 30);
        respawnOutsideDungeon = PlayerPrefs.GetInt(RespawnPrefsPrefix + "RespawnOutsideDungeon", 0) != 0;
        partyTravelInsideDungeon = PlayerPrefs.GetInt(RespawnPrefsPrefix + "PartyTravelInsideDungeon", 1) != 0;
        ClampRespawnSettings();
        localRespawnSettingsLoaded = true;
        usingRemoteRespawnSettings = false;
    }

    public static void EnsureLocalRespawnSettings()
    {
        // During a client session only the host's imported policy should be active.
        if (Mirror.NetworkClient.active && !Mirror.NetworkServer.active)
            return;
        if (!localRespawnSettingsLoaded || usingRemoteRespawnSettings)
            LoadLocalRespawnSettings();
    }

    public static void SaveLocalRespawnSettings()
    {
        if (Mirror.NetworkClient.active && !Mirror.NetworkServer.active)
            return;
        ClampRespawnSettings();
        PlayerPrefs.SetInt(RespawnPrefsPrefix + "ReviveHoldSeconds", reviveHoldSeconds);
        PlayerPrefs.SetInt(RespawnPrefsPrefix + "ManualRespawnSeconds", manualRespawnSeconds);
        PlayerPrefs.SetInt(RespawnPrefsPrefix + "ReviveHealthPercent", reviveHealthPercent);
        PlayerPrefs.SetInt(RespawnPrefsPrefix + "RespawnHealthPercent", respawnHealthPercent);
        PlayerPrefs.SetInt(RespawnPrefsPrefix + "RespawnOutsideDungeon", respawnOutsideDungeon ? 1 : 0);
        PlayerPrefs.SetInt(RespawnPrefsPrefix + "PartyTravelInsideDungeon", partyTravelInsideDungeon ? 1 : 0);
        PlayerPrefs.Save();
        localRespawnSettingsLoaded = true;
        usingRemoteRespawnSettings = false;
    }

    public static void RestoreRespawnDefaults()
    {
        if (Mirror.NetworkClient.active && !Mirror.NetworkServer.active)
            return;
        reviveHoldSeconds = 5;
        manualRespawnSeconds = 10;
        reviveHealthPercent = 30;
        respawnHealthPercent = 30;
        respawnOutsideDungeon = false;
        partyTravelInsideDungeon = true;
    }

    public static void ClampRespawnSettings()
    {
        reviveHoldSeconds = Mathf.Clamp(reviveHoldSeconds, 1, 10);
        manualRespawnSeconds = Mathf.Clamp(manualRespawnSeconds, 5, 30);
        reviveHealthPercent = Mathf.Clamp(reviveHealthPercent, 10, 50);
        respawnHealthPercent = Mathf.Clamp(respawnHealthPercent, 10, 50);
    }

    static int ReadInt(string[] fields, int index, int fallback, int min, int max)
    {
        int value;
        return index < fields.Length && int.TryParse(fields[index], out value)
            ? Mathf.Clamp(value, min, max) : fallback;
    }
	
	
	public static void Import(string s)
	{
		string[] list = (s ?? string.Empty).Split('#');
        if (list.Length < 5) return;
        // Session import deliberately does not write PlayerPrefs.
        usingRemoteRespawnSettings = true;
		timeHost = list[0] == "True";
		displayName = list[1] == "True";
		useHighestLevel = list[2] == "True";
		sendLocation = list[3] == "True";
		sendMessage = list[4] == "True";

        // Backward compatibility: five-field option strings come from older builds,
        // where MobileNpcSync was always enabled.
        SetMobileNpcSync(list.Length < 6 || list[5] == "True");
        reviveHoldSeconds = ReadInt(list, 6, 5, 1, 10);
        manualRespawnSeconds = ReadInt(list, 7, 10, 5, 30);
        reviveHealthPercent = ReadInt(list, 8, 30, 10, 50);
        respawnHealthPercent = ReadInt(list, 9, 30, 10, 50);
        respawnOutsideDungeon = list.Length >= 11 && list[10] == "True";
        partyTravelInsideDungeon = list.Length < 12 || list[11] == "True";
	}
	
    public static void SetMobileNpcSync(bool enabled)
    {
        mobileNpcSync = enabled;

        // A joining client can spawn its local player object before the host option RPC arrives.
        // Apply the imported host policy immediately so any temporary sync state is cleaned up,
        // or so a client that locally disabled the option can start it when the host enabled it.
        MobileNpcSync.ApplySessionOption(enabled);
    }

	public static string Export()
	{
        EnsureLocalRespawnSettings();
        ClampRespawnSettings();
        return timeHost + "#" + displayName + "#" + useHighestLevel + '#' + sendLocation + '#' + sendMessage + '#' + mobileNpcSync + '#' + reviveHoldSeconds + '#' + manualRespawnSeconds + '#' + reviveHealthPercent + '#' + respawnHealthPercent + '#' + respawnOutsideDungeon + '#' + partyTravelInsideDungeon;
	}
}

// DFU's HUD/fade is drawn by OnGUI at depth 0, above ordinary Unity canvases.
// Use the same font renderer at a foreground GUI depth, independently of HUD fade.
public class MultiplayerRespawnStatus : MonoBehaviour
{
    static MultiplayerRespawnStatus instance;
    string message;
    string[] lines = new string[0];
    float progress;
    int refreshedFrame = -1;

    public static void Show(string message, float progress)
    {
        if (instance == null)
            instance = new GameObject("Multiplayer respawn status").AddComponent<MultiplayerRespawnStatus>();
        instance.refreshedFrame = Time.frameCount;
        if (instance.message != message)
        {
            instance.message = message;
            instance.lines = (message ?? string.Empty).Split('\n');
        }
        instance.progress = progress;
    }

    static float MeasureTextWidth(DaggerfallWorkshop.Game.UserInterface.DaggerfallFont font,
        string text, Vector2 scale)
    {
        // CalculateTextWidth/GetGlyphWidth return font-layout units, including for SDF.
        // DrawText applies scale.x when advancing the pen.
        if (font.IsSDFCapable)
            return font.CalculateTextWidth(text, scale) * scale.x;

        // Match classic DrawText exactly: spaces have no extra glyph spacing, and
        // the trailing advance after the last glyph is not part of the visible text.
        byte[] characters = System.Text.Encoding.ASCII.GetBytes(text);
        float width = 0f;
        for (int i = 0; i < characters.Length; i++)
        {
            int code = font.HasGlyph(characters[i]) ? characters[i] : 32;
            width += font.GetGlyph(code).width * scale.x;
            if (code != 32 && i < characters.Length - 1)
                width += font.GlyphSpacing * scale.x;
        }
        return width;
    }

    void OnGUI()
    {
        // Set on every GUI event so Unity orders this behaviour ahead of the HUD at depth 0.
        GUI.depth = -10000;
        if (Event.current.type != EventType.Repaint || refreshedFrame != Time.frameCount ||
            !DaggerfallWorkshop.Game.MultiplayerRespawnManager.IsMultiplayerActive() ||
            DaggerfallWorkshop.Game.DaggerfallUI.Instance == null)
            return;

        var font = DaggerfallWorkshop.Game.DaggerfallUI.DefaultFont;
        if (font == null) return;
        var material = font.GetMaterial();
        if (material == null) return;

        Matrix4x4 oldMatrix = GUI.matrix;
        Color oldColor = GUI.color;
        Vector4 oldScissor = material.GetVector("_ScissorRect");
        try
        {
            GUI.matrix = Matrix4x4.identity;
            GUI.color = Color.white;
            // A previous DFU label can leave a clipped font material behind.
            material.SetVector("_ScissorRect", new Vector4(0, 1, 0, 1));
            Color yellow = DaggerfallWorkshop.Game.DaggerfallUI.DaggerfallDefaultTextColor;
            // Match the scale of DFU's classic 320x200 UI more closely.
            float scale = Mathf.Max(1f, Mathf.Min(Screen.width / 320f, Screen.height / 200f));
            float maxWidth = 0f;
            foreach (string line in lines)
                maxWidth = Mathf.Max(maxWidth, MeasureTextWidth(font, line, Vector2.one));
            if (maxWidth > 0f)
                scale = Mathf.Min(scale, (Screen.width - 16f) / maxWidth);
            Vector2 textScale = Vector2.one * scale;
            // Leave space for the normal top-screen location/quest messages.
            float y = Mathf.Round(Screen.height * 0.16f);
            float centreX = Screen.width * 0.5f;
            foreach (string line in lines)
            {
                float width = MeasureTextWidth(font, line, textScale);
                font.DrawText(line, new Vector2(Mathf.Round(centreX - width * 0.5f), y),
                    textScale, yellow, Color.black, Vector2.one * scale);
                y += (font.GlyphHeight + 4f) * scale;
            }
            if (progress >= 0f)
            {
                // Keep the existing bar size while enlarging the text; share the same centre.
                float barScale = Mathf.Max(1f, Mathf.Floor(Mathf.Min(Screen.width / 480f, Screen.height / 270f)));
                float width = 110f * barScale;
                Rect border = new Rect(Mathf.Round(centreX - width * 0.5f), y, width, 5f * barScale);
                GUI.color = new Color(0.55f, 0.43f, 0.12f, 1f);
                GUI.DrawTexture(border, Texture2D.whiteTexture);
                Rect inner = new Rect(border.x + barScale, border.y + barScale, border.width - 2f * barScale, 3f * barScale);
                GUI.color = Color.black;
                GUI.DrawTexture(inner, Texture2D.whiteTexture);
                inner.width *= Mathf.Clamp01(progress);
                GUI.color = yellow;
                GUI.DrawTexture(inner, Texture2D.whiteTexture);
            }
        }
        finally
        {
            material.SetVector("_ScissorRect", oldScissor);
            GUI.color = oldColor;
            GUI.matrix = oldMatrix;
        }
    }
}
