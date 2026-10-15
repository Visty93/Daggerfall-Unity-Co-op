using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop;
using Mirror;

public class PlayerAnimations : NetworkBehaviour
{
    // Kept the existing attack event and RPC cadence. Additional byte describes
    // the strike direction, so viewers can animate optional paperdoll motion.
    public SpriteMultiplayer sprite;

    void Start()
    {
        StartCoroutine(Check());
    }

    IEnumerator Check()
    {
        while (true)
        {
            // The local FPSWeapon belongs to the physical player, not to each
            // remote network proxy. Only the owning player should send attacks.
            if (!isLocalPlayer || PlayerMultiplayer.playerObject == null)
            {
                yield return new WaitForSeconds(0.12f);
                continue;
            }

            WeaponManager manager = PlayerMultiplayer.playerObject.GetComponent<WeaponManager>();
            FPSWeapon weapon = manager != null ? manager.ScreenWeapon : null;
            if (weapon != null && weapon.IsAttacking())
            {
                bool melee = weapon.WeaponType != WeaponTypes.Bow;
                byte direction = melee ? (byte)weapon.WeaponState : (byte)0;
                cmdPlayAttack(melee, direction);
                yield return new WaitForSeconds(1f);
            }
            yield return new WaitForSeconds(0.12f);
        }
    }

    [Command]
    void cmdPlayAttack(bool melee, byte direction)
    {
        rpcPlayAttack(melee, direction);
    }

    [ClientRpc]
    void rpcPlayAttack(bool melee, byte direction)
    {
        if (isLocalPlayer)
            return;

        // Preserve the existing animated sprite event (including werebeasts,
        // riding and players who have disabled paperdolls).
        if (sprite != null)
        {
            if (melee)
                sprite.playAttack();
            else
                sprite.playBow();
        }

        // Add a visual-only reaction to the same event on clients who have
        // opted in. No new RPC, attack calculation, or collider movement.
        PlayerAssets visuals = GetComponent<PlayerAssets>();
        if (visuals == null)
            visuals = GetComponentInParent<PlayerAssets>();
        if (visuals == null && sprite != null)
            visuals = sprite.GetComponentInParent<PlayerAssets>();
        if (visuals != null)
            visuals.TriggerRemotePaperdollAttack(melee, direction);
    }
}
