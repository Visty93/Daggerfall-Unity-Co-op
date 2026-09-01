using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallConnect;
using DaggerfallWorkshop.Utility;
using System.IO;

public class SpriteMultiplayer : MonoBehaviour
{
    [Header("Parameters")]
    public float minDistance = 0.05f;
    public float walkInterval = 0.5f;
    public SoundClips walkSound;
    public SoundClips attackSound;
    public SoundClips bowSound;
    public LayerMask groundLayer;

    [Header("Runtime")]
    public string job;
    public int gender;

    [Header("References")]
    public SetupDemoEnemy demoEnemy;
    public DaggerfallMobileUnit sprite;
    public MobileTypes type;
    public EnemySounds sound;
    public DaggerfallAudioSource aud;

    CharacterController controller;



    bool isAttacking = false;
    float timeWalk = 0f;
    Coroutine checkCoroutine = null;

    Vector3 baseLocalPosition;
    bool baseLocalPositionCaptured = false;
    MobileTypes lastAppliedType = (MobileTypes)(-999);
    int lastAppliedGender = -999;

    bool horseMounted = false;
    int lastMountedArchive = -1;
    bool warnedMissingMountedArchive = false;
    bool mountedIdleFrozen = false;

    // Cosmetic-only multiplayer lycanthrope state.
    // 0 = normal player, 1 = werewolf, 2 = wereboar.
    // This never changes the real local PlayerEntity or TransportManager state.
    int lycanthropeVisualState = 0;
    bool lycanthropeIdleFrozen = false;

    public bool IsLycanthropeVisualActive
    {
        get { return lycanthropeVisualState == 1 || lycanthropeVisualState == 2; }
    }

    [Header("Downed Visual")]
    public bool useCorpseVisualWhenDowned = true;
    public bool hideMobileUnitWhileDowned = true;
    public Vector3 corpseLocalOffset = Vector3.zero;

    [Tooltip("Parent corpse visual to PlayerMultiplayer root instead of the SpriteMultiplayer child. This keeps corpse height based on the network player ground/root position.")]
    public bool parentCorpseVisualToPlayerRoot = true;

    [Tooltip("Fixed local Y offset for the downed corpse under the PlayerMultiplayer root. Remote player roots are already around ground/feet level, while the corpse billboard pivot appears too high at 0.")]
    public float corpseGroundYOffset = -0.50f;

    GameObject downedCorpseVisual = null;
    Billboard downedCorpseBillboard = null;
    bool downedVisualActive = false;

    void Awake()
    {
        CaptureBaseLocalPosition();
    }

    void Start()
    {
        controller = GetComponent<CharacterController>();
        CaptureBaseLocalPosition();

        // Do not apply a default/empty profile in Start(). PlayerAssets will call
        // RefreshProfile() once the remote SyncVars contain a stable real character.
        if (HasUsableProfile())
            ApplyCurrentProfile();
    }

    void OnEnable()
    {
        CaptureBaseLocalPosition();
        if (checkCoroutine == null)
            checkCoroutine = StartCoroutine(Check());
    }

    void OnDisable()
    {
        if (checkCoroutine != null)
        {
            StopCoroutine(checkCoroutine);
            checkCoroutine = null;
        }
    }

    private void CaptureBaseLocalPosition()
    {
        if (baseLocalPositionCaptured)
            return;

        baseLocalPosition = transform.localPosition;
        baseLocalPositionCaptured = true;
    }

    private void RestoreBaseLocalPosition()
    {
        if (baseLocalPositionCaptured)
            transform.localPosition = baseLocalPosition;
    }

    private bool HasUsableProfile()
    {
        return !string.IsNullOrEmpty(job);
    }

    public void RefreshProfile(string newJob, int newGender)
    {
        if (string.IsNullOrEmpty(newJob))
            return;

        job = newJob;
        gender = newGender;

        // MP visual priority: transformed beast > mounted rider > normal class.
        if (IsLycanthropeVisualActive)
            ApplyLycanthropeVisual();
        else if (horseMounted)
            ApplyMountedVisual();
        else
            ApplyCurrentProfile();
    }

    public void SetLycanthropeVisualState(int state)
    {
        state = Mathf.Clamp(state, 0, 2);
        if (lycanthropeVisualState == state)
        {
            // Initial SyncVars can arrive before the remote profile is ready. Re-apply once
            // PlayerAssets explicitly calls us after enabling/configuring the remote visual.
            if (!downedVisualActive && HasUsableProfile())
            {
                if (IsLycanthropeVisualActive)
                    ApplyLycanthropeVisual();
            }
            else if (downedVisualActive)
            {
                ApplyDownedCorpseVisual();
            }
            return;
        }

        lycanthropeVisualState = state;
        lycanthropeIdleFrozen = false;

        if (sprite != null)
            sprite.FreezeAnims = false;

        // While downed, rebuild only the corpse material. Revive will restore the correct
        // alive beast/mount/class visual using the latest synchronized states.
        if (downedVisualActive)
        {
            ApplyDownedCorpseVisual();
            return;
        }

        if (!HasUsableProfile())
            return;

        if (IsLycanthropeVisualActive)
            ApplyLycanthropeVisual();
        else if (horseMounted)
            ApplyMountedVisual();
        else
            ApplyCurrentProfile(true);
    }

    public void SetMountedState(bool mounted)
    {
        if (horseMounted == mounted)
            return;

        horseMounted = mounted;

        // While downed, only remember the transport state. Revive restores the
        // appropriate alive visual after the corpse presentation has been removed.
        if (downedVisualActive || !HasUsableProfile())
            return;

        // A transformed player can still be on a horse in DFU. The horse state remains
        // synchronized, but it must not replace the remote werewolf/wereboar sprite.
        if (IsLycanthropeVisualActive)
            ApplyLycanthropeVisual();
        else if (horseMounted)
            ApplyMountedVisual();
        else
            ApplyCurrentProfile(true);
    }

    private void ApplyLycanthropeVisual()
    {
        if (!IsLycanthropeVisualActive || sprite == null || DaggerfallUnity.Instance == null)
            return;

        MobileTypes beastType =
            lycanthropeVisualState == 1 ? MobileTypes.Werewolf : MobileTypes.Wereboar;

        type = beastType;
        lastAppliedType = beastType;
        lastAppliedGender = gender;
        lastMountedArchive = -1;
        mountedIdleFrozen = false;
        lycanthropeIdleFrozen = false;

        sprite.FreezeAnims = false;

        // Reuse DFU's native monster definition. This supplies the correct classic archive,
        // 8-direction movement, primary attack, hurt frames, scaling, mirroring, and corpse.
        if (demoEnemy != null)
            demoEnemy.ApplyEnemySettings(
                beastType,
                MobileReactions.Passive,
                MobileGender.Male,
                0,
                true);

        // Werewolf/wereboar have no dedicated idle set. Start from movement and let Check()
        // freeze the first movement pose while the network player is stationary.
        sprite.ChangeEnemyState(MobileStates.Move);
        sprite.FreezeAnims = true;
        lycanthropeIdleFrozen = true;

        RestoreBaseLocalPosition();

        if (downedVisualActive)
            ApplyDownedCorpseVisual();
    }

    private void ApplyCurrentProfile(bool force = false)
    {
        if (!HasUsableProfile())
            return;

        if (controller == null)
            controller = GetComponent<CharacterController>();

        MobileTypes newType = getCorrectType(job);
        MobileGender mobileGender = (gender == 0 ? MobileGender.Male : MobileGender.Female);

        // Avoid re-applying the same enemy settings every time PlayerAssets refreshes.
        // Dismounting uses force=true because the DaggerfallMobileUnit currently contains
        // a mounted visual even though the underlying job/gender did not change.
        if (!force && newType == lastAppliedType && gender == lastAppliedGender)
        {
            RestoreBaseLocalPosition();
            return;
        }

        type = newType;
        lastAppliedType = newType;
        lastAppliedGender = gender;
        lastMountedArchive = -1;
        mountedIdleFrozen = false;
        lycanthropeIdleFrozen = false;

        if (sprite != null)
            sprite.FreezeAnims = false;

        if (demoEnemy != null)
            demoEnemy.ApplyEnemySettings(type, MobileReactions.Passive, mobileGender, 0, true);

        if (sprite != null)
            sprite.ChangeEnemyState(MobileStates.Idle);

        // Do not run GameObjectHelper.AlignControllerToGround() here. The remote
        // PlayerMultiplayer root already owns the networked ground position. Re-aligning
        // this visual child after a class/sprite change can push mage-style sprites a foot
        // or two below the root on some clients. Keep the prefab's original local offset.
        RestoreBaseLocalPosition();

        // If a profile refresh arrives while this remote player is downed, keep the
        // corpse visual active and hidden mobile sprite hidden. The refreshed type/gender
        // will still be used next time the corpse texture is rebuilt.
        if (downedVisualActive)
            ApplyDownedCorpseVisual();
    }


    private void ApplyMountedVisual()
    {
        if (IsLycanthropeVisualActive)
        {
            ApplyLycanthropeVisual();
            return;
        }

        if (!HasUsableProfile() || sprite == null || DaggerfallUnity.Instance == null)
            return;

        int mountedArchive = GetMountedArchive(job, gender);
        if (mountedArchive <= 0)
        {
            ApplyCurrentProfile(true);
            return;
        }

        // These are original classic Daggerfall mobile archives. DaggerfallMobileUnit
        // needs the raw archive for record sizes/frame counts even when texture
        // replacement artwork is available. Fall back cleanly if this installation
        // does not contain the unused archive.
        if (!MountedArchiveExists(mountedArchive))
        {
            if (!warnedMissingMountedArchive)
            {
                warnedMissingMountedArchive = true;
                Debug.LogWarning(
                    "[SpriteMultiplayer] Mounted archive " + mountedArchive +
                    " was not found in ARENA2. Keeping the normal remote player visual.");
            }

            ApplyCurrentProfile(true);
            return;
        }

        MobileTypes templateType = GetMountedTemplateType(mountedArchive);
        MobileEnemy mountedEnemy;
        if (!GameObjectHelper.EnemyDict.TryGetValue((int)templateType, out mountedEnemy))
        {
            Debug.LogWarning(
                "[SpriteMultiplayer] Could not find mounted visual template " +
                templateType + " for archive " + mountedArchive + ".");
            ApplyCurrentProfile(true);
            return;
        }

        MobileGender mobileGender =
            (gender == 0 ? MobileGender.Male : MobileGender.Female);

        // Use the animation definition from the rider family that the mounted archive
        // was made from, rather than the player's unrelated class animation table.
        // Only the visual DaggerfallMobileUnit is changed; PlayerMultiplayer gameplay,
        // collision, authority, and the real local PlayerAdvanced remain untouched.
        mountedEnemy.Gender = mobileGender;
        mountedEnemy.MaleTexture = mountedArchive;
        mountedEnemy.FemaleTexture = mountedArchive;
        mountedEnemy.Reactions = MobileReactions.Passive;

        // The unused mounted archives do not have the ordinary humanoid idle/ranged/
        // spell record ranges. Mark those states unavailable so DaggerfallMobileUnit
        // uses the movement orientations for Idle instead of requesting missing records.
        mountedEnemy.PrimaryAttackAnimFrames = new int[] { 1, 2, 3, 4, 5, 6, 7, -1, 0 };
        mountedEnemy.ChanceForAttack2 = 0;
        mountedEnemy.ChanceForAttack3 = 0;
        mountedEnemy.ChanceForAttack4 = 0;
        mountedEnemy.ChanceForAttack5 = 0;
        mountedEnemy.HasIdle = false;
        mountedEnemy.HasRangedAttack1 = false;
        mountedEnemy.HasRangedAttack2 = false;
        mountedEnemy.HasSpellAnimation = false;

        sprite.FreezeAnims = false;
        mountedIdleFrozen = false;

        sprite.SetEnemy(
            DaggerfallUnity.Instance,
            mountedEnemy,
            MobileReactions.Passive,
            sprite.ClassicSpawnDistanceType);

        sprite.ChangeEnemyState(MobileStates.Idle);
        lastMountedArchive = mountedArchive;
        RestoreBaseLocalPosition();
    }

    private bool MountedArchiveExists(int archive)
    {
        try
        {
            if (DaggerfallUnity.Instance == null ||
                string.IsNullOrEmpty(DaggerfallUnity.Instance.Arena2Path))
                return false;

            string fileName = string.Format("TEXTURE.{0:000}", archive);
            string path = Path.Combine(DaggerfallUnity.Instance.Arena2Path, fileName);
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private int GetMountedArchive(string currentJob, int currentGender)
    {
        bool female = currentGender != 0;

        switch (currentJob)
        {
            // 487/488 -> 493/494.
            case "Knight":
            case "Warrior":
            case "Barbarian":
            case "Monk":
                return female ? 493 : 494;

            // Ranger/Archer use 481/482 on foot.
            // Female has the exact 481 -> 495 mounted counterpart.
            // There is no known male 482 -> 496 mounted set, so male falls back to 494.
            case "Ranger":
            case "Archer":
                return female ? 495 : 494;

            // Exact Spellsword pair plus closest available magic/light fallback.
            case "Spellsword":
            case "Battlemage":
            case "Nightblade":
            case "Mage":
            case "Sorcerer":
            case "Healer":
            case "Bard":
            case "Thief":
            case "Burglar":
            case "Acrobat":
            case "Rogue":
            case "Assassin":
                return female ? 491 : 492;

            // Unknown/custom classes use the normal MP Thief visual on foot.
            // Use the medium rider family as the closest mounted match.
            default:
                return female ? 493 : 494;
        }
    }

    private MobileTypes GetMountedTemplateType(int mountedArchive)
    {
        switch (mountedArchive)
        {
            // Match the animation definition to the classic mobile family whose
            // texture archive was used to make the mounted set:
            // 477/478 -> 491/492, 487/488 -> 493/494, 481 -> 495.
            case 493:
            case 494:
                return MobileTypes.Warrior;

            case 495:
                return MobileTypes.Ranger;

            case 491:
            case 492:
            default:
                return MobileTypes.Sorcerer;
        }
    }

    public void SetDownedVisual(bool downed)
    {
        if (downedVisualActive == downed)
            return;

        downedVisualActive = downed;

        if (downed)
            ApplyDownedCorpseVisual();
        else
            RestoreAliveVisual();
    }

    private void ApplyDownedCorpseVisual()
    {
        if (!useCorpseVisualWhenDowned)
            return;

        EnsureDownedCorpseVisual();

        if (downedCorpseVisual != null)
            downedCorpseVisual.SetActive(true);

        if (sprite != null)
        {
            // Never carry a mounted animation freeze into the downed/revive lifecycle.
            sprite.FreezeAnims = false;
            mountedIdleFrozen = false;
            lycanthropeIdleFrozen = false;

            // Stop current attack/move animation before hiding the mobile unit.
            sprite.ChangeEnemyState(MobileStates.Idle);
            sprite.enabled = false;
        }

        if (hideMobileUnitWhileDowned)
            SetMobileUnitRenderersVisible(false);

        RestoreBaseLocalPosition();
    }

    private void RestoreAliveVisual()
    {
        if (downedCorpseVisual != null)
            downedCorpseVisual.SetActive(false);

        if (IsLycanthropeVisualActive)
            ApplyLycanthropeVisual();
        else if (horseMounted)
            ApplyMountedVisual();
        else
            ApplyCurrentProfile(true);

        SetMobileUnitRenderersVisible(true);

        if (sprite != null)
        {
            sprite.enabled = true;
            sprite.ChangeEnemyState(MobileStates.Idle);
        }

        RestoreBaseLocalPosition();
    }

    private void EnsureDownedCorpseVisual()
    {
        int archive;
        int record;
        if (!TryGetCurrentCorpseTexture(out archive, out record))
            return;

        Transform corpseParent = GetDownedCorpseParent();
        if (corpseParent == null)
            corpseParent = transform;

        if (downedCorpseVisual == null)
        {
            GameObject prefab = null;
            if (DaggerfallUnity.Instance != null && DaggerfallUnity.Instance.Option_LootContainerPrefab != null)
                prefab = DaggerfallUnity.Instance.Option_LootContainerPrefab.gameObject;

            if (prefab != null)
                downedCorpseVisual = GameObjectHelper.InstantiatePrefab(prefab, "MP Downed Corpse Visual", corpseParent, corpseParent.position);
            else
                downedCorpseVisual = new GameObject("MP Downed Corpse Visual");

            downedCorpseVisual.transform.parent = corpseParent;
            downedCorpseVisual.transform.localRotation = Quaternion.identity;
            downedCorpseVisual.transform.localScale = Vector3.one;

            // This is a visual-only corpse. Do not let it become lootable or block clicks
            // intended for the downed PlayerMultiplayer revive raycast.
            Component lootComponent = downedCorpseVisual.GetComponent("DaggerfallLoot");
            if (lootComponent != null)
                Destroy(lootComponent);

            Collider[] colliders = downedCorpseVisual.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
                Destroy(colliders[i]);

            downedCorpseBillboard = downedCorpseVisual.GetComponentInChildren<Billboard>(true);
            if (downedCorpseBillboard == null)
                downedCorpseBillboard = downedCorpseVisual.AddComponent<Billboard>();
        }
        else if (downedCorpseVisual.transform.parent != corpseParent)
        {
            downedCorpseVisual.transform.parent = corpseParent;
            downedCorpseVisual.transform.localRotation = Quaternion.identity;
            downedCorpseVisual.transform.localScale = Vector3.one;
        }

        if (downedCorpseBillboard != null)
        {
            downedCorpseBillboard.SetMaterial(archive, record);

            Vector3 localPosition = corpseLocalOffset;

            // The visual-only corpse is parented to the PlayerMultiplayer root, whose origin is
            // already near the remote player's feet/ground position. DFU corpse billboards do not
            // need the normal half-height lift here. In fact, even local Y=0 is too high on this
            // prefab, so apply a small negative Y offset instead. This is intentionally fixed and
            // not based on billboard height, because different fake-player mobiles share the same
            // grounded root but have different visual child offsets.
            if (corpseParent != transform)
                localPosition.y += corpseGroundYOffset;

            downedCorpseVisual.transform.localPosition = localPosition;
        }
    }

    private Transform GetDownedCorpseParent()
    {
        if (parentCorpseVisualToPlayerRoot)
        {
            PlayerMultiplayer playerMultiplayer = GetComponentInParent<PlayerMultiplayer>();
            if (playerMultiplayer != null)
                return playerMultiplayer.transform;
        }

        return transform;
    }

    private bool TryGetCurrentCorpseTexture(out int archive, out int record)
    {
        archive = 0;
        record = 0;

        MobileEnemy mobileEnemy;
        if (!GameObjectHelper.EnemyDict.TryGetValue((int)type, out mobileEnemy))
            return false;

        int corpseTexture = mobileEnemy.CorpseTexture;
        if (gender != 0 && mobileEnemy.FemaleCorpseTexture != 0)
            corpseTexture = mobileEnemy.FemaleCorpseTexture;

        if (corpseTexture == 0)
            return false;

        EnemyBasics.ReverseCorpseTexture(corpseTexture, out archive, out record);
        return true;
    }

    private void SetMobileUnitRenderersVisible(bool visible)
    {
        if (sprite == null)
            return;

        Renderer[] renderers = sprite.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null)
                continue;

            if (downedCorpseVisual != null && r.transform.IsChildOf(downedCorpseVisual.transform))
                continue;

            r.enabled = visible;
        }
    }

    IEnumerator Check()
    {
        yield return new WaitForSeconds(0.1f);
        Vector3 lastPos = transform.position;lastPos.y = 0;
        while(true)
        {
            if (!downedVisualActive && sprite != null && !sprite.IsPlayingOneShot()){
                Vector3 actualPos = transform.position; actualPos.y = 0;
                bool moving = Vector3.Distance(lastPos, actualPos) > minDistance;

                if (moving){
                    // Mounted and werebeast idle both use FreezeAnims. Always release that
                    // frozen first movement pose before the remote player starts moving.
                    if (lastMountedArchive > 0 || IsLycanthropeVisualActive){
                        if (sprite.FreezeAnims)
                            sprite.FreezeAnims = false;
                        mountedIdleFrozen = false;
                        lycanthropeIdleFrozen = false;
                    }

                    sprite.ChangeEnemyState(MobileStates.Move);
                    lastPos = actualPos;
                    if (Time.time > timeWalk+ walkInterval){
                        timeWalk = Time.time;
                        if (aud != null)
                            aud.PlayOneShot(walkSound);
                    }
                }else{
                    if (IsLycanthropeVisualActive){
                        // Werewolf/wereboar have no dedicated idle animation. Match the mounted
                        // visual rule: reset to movement pose and freeze the first frame.
                        if (!lycanthropeIdleFrozen || !sprite.FreezeAnims){
                            sprite.FreezeAnims = false;
                            sprite.ChangeEnemyState(MobileStates.Move);
                            sprite.FreezeAnims = true;
                            lycanthropeIdleFrozen = true;
                        }
                        mountedIdleFrozen = false;
                    }else if (lastMountedArchive > 0){
                        // Mounted archives have no true humanoid idle animation.
                        // Reset to the first movement pose once, then freeze that frame
                        // until the remote player actually moves again.
                        if (!mountedIdleFrozen || !sprite.FreezeAnims){
                            sprite.FreezeAnims = false;
                            sprite.ChangeEnemyState(MobileStates.Move);
                            sprite.FreezeAnims = true;
                            mountedIdleFrozen = true;
                        }
                        lycanthropeIdleFrozen = false;
                    }else{
                        // Never allow a special visual freeze to leak into normal on-foot visuals.
                        if (sprite.FreezeAnims)
                            sprite.FreezeAnims = false;
                        mountedIdleFrozen = false;
                        lycanthropeIdleFrozen = false;
                        sprite.ChangeEnemyState(MobileStates.Idle);
                    }
                }
            }

            // Keep the visual child at the same local offset under its networked root.
            // The old ground-align pass could preserve the wrong offset after a temporary
            // class/profile switch, especially with mage-type visuals.
            RestoreBaseLocalPosition();

            yield return new WaitForSeconds(0.1f);
        }
    }

    public void playAttack()
    {
        if (downedVisualActive)
            return;

        if (aud != null)
            aud.PlayOneShot(attackSound);

        if (sprite != null)
        {
            // A stationary mounted player or werebeast can be frozen on the first movement
            // frame. Release that freeze before starting the native primary attack.
            if (lastMountedArchive > 0 || IsLycanthropeVisualActive)
            {
                if (sprite.FreezeAnims)
                    sprite.FreezeAnims = false;
                mountedIdleFrozen = false;
                lycanthropeIdleFrozen = false;
            }

            sprite.ChangeEnemyState(MobileStates.PrimaryAttack);
        }

    }

    public void playBow()
    {
        if (downedVisualActive)
            return;

        if (aud != null)
            aud.PlayOneShot(bowSound);

        // Keep the mounted pose until horse ranged frames are explicitly verified.
        // Native werewolf/wereboar mobiles also have no ranged attack animation.
        if (lastMountedArchive > 0 || IsLycanthropeVisualActive)
            return;

        if (sprite != null)
            sprite.ChangeEnemyState(MobileStates.RangedAttack1);
    }

    public void playHurt()
    {
        // Do not change the existing human-player hurt presentation. This helper is only
        // for transformed remote players, driven by the already-synchronized MP health.
        if (downedVisualActive || !IsLycanthropeVisualActive || sprite == null)
            return;

        if (sprite.FreezeAnims)
            sprite.FreezeAnims = false;

        lycanthropeIdleFrozen = false;
        mountedIdleFrozen = false;
        sprite.ChangeEnemyState(MobileStates.Hurt);
    }

    MobileTypes getCorrectType(string s)
    {
        switch (s){
            case "Spellsword":
                return MobileTypes.Spellsword;
            case "Warrior":
                return MobileTypes.Warrior;
            case "Battlemage":
                return MobileTypes.Battlemage;
            case "Sorcerer":
                return MobileTypes.Sorcerer;
            case "Bard":
                return MobileTypes.Bard;
            case "Mage":
                return MobileTypes.Mage;
            case "Healer":
                return MobileTypes.Healer;
            case "Nightblade":
                return MobileTypes.Nightblade;
            case "Burglar":
                return MobileTypes.Burglar;
            case "Acrobat":
                return MobileTypes.Acrobat;
            case "Rogue":
                return MobileTypes.Rogue;
            case "Assassin":
                return MobileTypes.Assassin;
            case "Archer":
                return MobileTypes.Archer;
            case "Ranger":
                return MobileTypes.Ranger;
            case "Monk":
                return MobileTypes.Monk;
            case "Barbarian":
                return MobileTypes.Barbarian;
            case "Knight":
                return MobileTypes.Knight;

        }
        return MobileTypes.Thief;
    }

}
