// Shared display-only portrait treatment for party UI.
// Uses the already-synchronized PlayerMultiplayer life state. No network messages or gameplay changes.
using System;
using System.Collections.Generic;
using UnityEngine;

public static class MultiplayerPartyPortraitStatus
{
    private static readonly Dictionary<Texture2D, Texture2D> GrayPortraits =
        new Dictionary<Texture2D, Texture2D>();

    public static bool IsDowned(PlayerMultiplayer player)
    {
        if (player == null)
            return false;

        // Keep the portrait grayscale during the brief Respawning state, until Alive.
        // The health fallback covers packets arriving before the life-state SyncVar.
        return player.LifeState != PlayerMultiplayer.MultiplayerLifeState.Alive ||
               (player.PlayerMPMaxHealth > 0 && player.PlayerMPCurrentHealth <= 0);
    }

    public static Texture2D DisplayTexture(Texture2D normalPortrait, bool downed)
    {
        if (!downed || normalPortrait == null)
            return normalPortrait;

        Texture2D grayscale;
        if (GrayPortraits.TryGetValue(normalPortrait, out grayscale) && grayscale != null)
            return grayscale;

        try
        {
            Color32[] pixels = ReadPixels(normalPortrait);
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 pixel = pixels[i];
                byte brightness = (byte)Mathf.Clamp(Mathf.RoundToInt(
                    pixel.r * 0.299f + pixel.g * 0.587f + pixel.b * 0.114f), 0, 255);
                pixels[i] = new Color32(brightness, brightness, brightness, pixel.a);
            }

            grayscale = new Texture2D(normalPortrait.width, normalPortrait.height, TextureFormat.RGBA32, false);
            grayscale.name = normalPortrait.name + " (party downed)";
            grayscale.filterMode = normalPortrait.filterMode;
            grayscale.wrapMode = normalPortrait.wrapMode;
            grayscale.hideFlags = HideFlags.DontSave;
            grayscale.SetPixels32(pixels);
            grayscale.Apply(false, true);
            GrayPortraits[normalPortrait] = grayscale;
            return grayscale;
        }
        catch (Exception ex)
        {
            // Preserve portrait/UI functionality even if this platform cannot read a texture.
            Debug.LogWarning("[PartyPortrait] Unable to generate grayscale portrait: " + ex.Message);
            return normalPortrait;
        }
    }

    private static Color32[] ReadPixels(Texture2D texture)
    {
        if (texture.isReadable)
            return texture.GetPixels32();

        // Some image loaders return non-readable textures; read back this tiny portrait
        // only on first downed transition. Never do this every frame.
        RenderTexture target = RenderTexture.GetTemporary(
            texture.width, texture.height, 0, RenderTextureFormat.ARGB32);
        RenderTexture old = RenderTexture.active;
        Texture2D readable = null;
        try
        {
            Graphics.Blit(texture, target);
            RenderTexture.active = target;
            readable = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
            readable.Apply(false, false);
            return readable.GetPixels32();
        }
        finally
        {
            RenderTexture.active = old;
            RenderTexture.ReleaseTemporary(target);
            if (readable != null)
                UnityEngine.Object.Destroy(readable);
        }
    }
}
