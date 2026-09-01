using System;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;

/// <summary>
/// Multiplayer bridge for friendly player-to-player spell targeting.
///
/// PlayerMultiplayer shells are only target markers. The real spell effect is
/// applied on the target owner's local PlayerAdvanced / PlayerEntityBehaviour by
/// PlayerMultiplayer.TargetApplyFriendlyPlayerSpell().
///
/// This intentionally does not enable PvP. Only bundles whose effects pass the
/// friendly whitelist are forwarded.
/// </summary>
public static class PlayerSpellMultiplayerBridge
{
    public const float FriendlyPlayerTouchRange = DaggerfallMissile.TouchRange;
    public const float FriendlyPlayerTouchSphereRadius = DaggerfallMissile.SphereCastRadius;
    public const float ServerTouchValidationDistance = 6.0f;
    public const float ServerRangedValidationDistance = 96.0f;
    public const float ServerAreaValidationDistance = 96.0f;

    static uint nextLocalFriendlySpellCastId = 1;

    public static bool TryGetPlayerMultiplayerFromCollider(Collider collider, out PlayerMultiplayer player)
    {
        player = null;
        if (collider == null)
            return false;

        player = collider.GetComponentInParent<PlayerMultiplayer>();
        return player != null;
    }

    public static bool TryGetPlayerMultiplayerFromHit(Collision collision, Collider other, out PlayerMultiplayer player)
    {
        player = null;

        if (other != null)
            return TryGetPlayerMultiplayerFromCollider(other, out player);

        if (collision != null && collision.gameObject != null)
        {
            player = collision.gameObject.GetComponentInParent<PlayerMultiplayer>();
            return player != null;
        }

        return false;
    }

    public static bool TryGetPlayerMultiplayerTargetInTouchRange(Vector3 aimPosition, Vector3 aimDirection, out PlayerMultiplayer player)
    {
        player = null;

        aimPosition -= aimDirection * 0.1f;
        Ray ray = new Ray(aimPosition, aimDirection);
        RaycastHit hit;
        if (!Physics.SphereCast(ray, FriendlyPlayerTouchSphereRadius, out hit, FriendlyPlayerTouchRange))
            return false;

        return TryGetPlayerMultiplayerFromCollider(hit.collider, out player);
    }

    public static bool CanForwardLocalFriendlyPlayerSpell(EntityEffectBundle payload, PlayerMultiplayer targetPlayer)
    {
        string reason;
        return CanForwardLocalFriendlyPlayerSpell(payload, targetPlayer, out reason);
    }

    public static bool CanForwardLocalFriendlyPlayerSpell(EntityEffectBundle payload, PlayerMultiplayer targetPlayer, out string reason)
    {
        reason = string.Empty;

        if (payload == null)
        {
            reason = "payload is null";
            return false;
        }

        if (targetPlayer == null)
        {
            reason = "target PlayerMultiplayer is null";
            return false;
        }

        if (!(NetworkClient.active || NetworkServer.active))
        {
            reason = "network is not active";
            return false;
        }

        if (payload.CasterEntityBehaviour == null || GameManager.Instance == null ||
            payload.CasterEntityBehaviour != GameManager.Instance.PlayerEntityBehaviour)
        {
            reason = "caster is not the real local PlayerAdvanced";
            return false;
        }

        PlayerMultiplayer localPlayer = PlayerMultiplayer.GetLocalPlayerForCommand("friendly player spell");
        if (localPlayer == null)
        {
            reason = "local PlayerMultiplayer command owner not found";
            return false;
        }

        if (targetPlayer.netId == 0 || targetPlayer.netId == localPlayer.netId || targetPlayer.isLocalPlayer)
        {
            reason = "target is self or has no valid netId";
            return false;
        }

        if (!IsFriendlyPlayerSpellBundle(payload.Settings, out reason))
            return false;

        return true;
    }

    public static bool TryForwardLocalFriendlyPlayerSpell(EntityEffectBundle payload, PlayerMultiplayer targetPlayer, Vector3 impactPosition, string context)
    {
        string reason;
        if (!CanForwardLocalFriendlyPlayerSpell(payload, targetPlayer, out reason))
        {
            if (Debug.isDebugBuild && targetPlayer != null)
                Debug.Log($"[FriendlyPlayerSpell][Skip:{context}] target={targetPlayer.netId} reason={reason}");
            return false;
        }

        PlayerMultiplayer localPlayer = PlayerMultiplayer.GetLocalPlayerForCommand("friendly player spell forward");
        if (localPlayer == null)
            return false;

        string spellData = JsonUtility.ToJson(payload.Settings);
        int casterLevel = 1;
        try
        {
            if (GameManager.Instance != null && GameManager.Instance.PlayerEntity != null)
                casterLevel = Mathf.Clamp(GameManager.Instance.PlayerEntity.Level, 1, 100);
        }
        catch { casterLevel = 1; }

        uint castId = nextLocalFriendlySpellCastId++;
        if (nextLocalFriendlySpellCastId == 0)
            nextLocalFriendlySpellCastId = 1;

        localPlayer.CmdRequestFriendlyPlayerSpell(targetPlayer.netId, spellData, casterLevel, impactPosition, castId);

        if (Debug.isDebugBuild)
            Debug.Log($"[FriendlyPlayerSpell][Forward:{context}] source={localPlayer.netId} target={targetPlayer.netId} castId={castId} effects={payload.Settings.Effects.Length}");

        return true;
    }

    public static bool ServerValidateFriendlyPlayerSpell(PlayerMultiplayer sourcePlayer, PlayerMultiplayer targetPlayer, EffectBundleSettings settings, out string reason)
    {
        reason = string.Empty;

        if (sourcePlayer == null || targetPlayer == null)
        {
            reason = "source or target missing";
            return false;
        }

        if (sourcePlayer == targetPlayer || sourcePlayer.netId == targetPlayer.netId)
        {
            reason = "source equals target";
            return false;
        }

        if (!IsFriendlyPlayerSpellBundle(settings, out reason))
            return false;

        float maxDistance;
        switch (settings.TargetType)
        {
            case TargetTypes.ByTouch:
                maxDistance = ServerTouchValidationDistance;
                break;
            case TargetTypes.SingleTargetAtRange:
            case TargetTypes.AreaAtRange:
                maxDistance = ServerRangedValidationDistance;
                break;
            case TargetTypes.AreaAroundCaster:
                maxDistance = ServerAreaValidationDistance;
                break;
            default:
                reason = "unsupported target type for remote friendly player spell";
                return false;
        }

        float distance = Vector3.Distance(sourcePlayer.transform.position, targetPlayer.transform.position);
        if (distance > maxDistance)
        {
            reason = $"target too far ({distance:0.00} > {maxDistance:0.00})";
            return false;
        }

        return true;
    }

    public static bool IsFriendlyPlayerSpellBundle(EffectBundleSettings settings, out string reason)
    {
        reason = string.Empty;

        if (settings.Effects == null || settings.Effects.Length == 0)
        {
            reason = "spell has no effects";
            return false;
        }

        if (settings.TargetType == TargetTypes.CasterOnly || settings.TargetType == TargetTypes.None)
        {
            reason = "caster-only/none target type is not remote-targetable";
            return false;
        }

        for (int i = 0; i < settings.Effects.Length; i++)
        {
            string key = settings.Effects[i].Key;
            if (!IsFriendlyPlayerEffectKey(key))
            {
                reason = "blocked effect key: " + (string.IsNullOrEmpty(key) ? "<empty>" : key);
                return false;
            }
        }

        return true;
    }

    public static bool IsFriendlyPlayerEffectKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            return false;

        string k = NormalizeEffectKey(key);

        // Explicitly block common hostile/debuff families first. Cure/restore/resist
        // are checked later and should not be blocked by containing words like poison.
        if (k.StartsWith("damage") || k.StartsWith("drain") || k.StartsWith("lower") ||
            k.StartsWith("weakness") || k.StartsWith("disintegrate") || k.StartsWith("soultrap") ||
            k == "silence" || k == "paralyze" || k == "paralysis" ||
            k == "poison" || k == "disease")
            return false;

        // Friendly families. This keeps the transport generic without making every
        // individual spell a custom network case. Unknown effects stay blocked.
        if (k.StartsWith("heal") || k.StartsWith("restore") || k.StartsWith("cure") ||
            k.StartsWith("fortify") || k.StartsWith("resist"))
            return true;

        switch (k)
        {
            case "shield":
            case "spellreflection":
            case "spellabsorption":
            case "waterbreathing":
            case "waterwalking":
            case "levitate":
            case "slowfalling":
            case "slowfall":
            case "light":
            case "invisibility":
            case "chameleon":
            case "freeaction":
                return true;
        }

        return false;
    }

    public static bool IsInstantOneShotFriendlyPlayerBundle(EffectBundleSettings settings)
    {
        if (settings.Effects == null || settings.Effects.Length == 0)
            return false;

        for (int i = 0; i < settings.Effects.Length; i++)
        {
            string key = settings.Effects[i].Key;
            if (string.IsNullOrEmpty(key))
                return false;

            string k = NormalizeEffectKey(key);

            // These are immediate friendly effects. They should run once on the real
            // target PlayerAdvanced, then be removed so the target HUD does not keep
            // a stuck active-effect icon. Duration-based buffs like Fortify/Resist/
            // Shield/Levitate are intentionally not included here.
            if (!(k.StartsWith("heal") || k.StartsWith("restore") || k.StartsWith("cure")))
                return false;
        }

        return true;
    }

    public static AssignBundleFlags GetFriendlyPlayerAssignBundleFlags()
    {
        // Treat every connected PlayerMultiplayer as a lightweight party member for now.
        // Friendly support spells forwarded through this bridge should always land on
        // the target player's real local PlayerAdvanced:
        // - BypassSavingThrows prevents "Save vs spell" / resistance/reflection checks.
        // - BypassChance prevents chance-based friendly effects from failing on cast.
        // - ShowNonPlayerFailures keeps existing diagnostics/messages for unexpected issues.
        return AssignBundleFlags.ShowNonPlayerFailures |
               AssignBundleFlags.BypassSavingThrows |
               AssignBundleFlags.BypassChance;
    }

    static string NormalizeEffectKey(string key)
    {
        return key.ToLowerInvariant()
            .Replace(" ", string.Empty)
            .Replace("-", string.Empty)
            .Replace("_", string.Empty);
    }
}
