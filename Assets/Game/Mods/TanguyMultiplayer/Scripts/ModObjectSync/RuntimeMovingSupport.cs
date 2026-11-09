// Opt-in local passenger support for moving presentation replicas.
// It never parents a player, claims an object, or runs a source mod's scripts.
using UnityEngine;
using System.Collections.Generic;
using Mirror;
using DaggerfallCoop.API;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Serialization;

namespace DaggerfallCoop.ObjectSync
{
    // MotionVisualView advances at 31000; carry the passenger by the same final
    // rendered platform delta. Native PlayerMotor still handles walking/gravity.
    [DefaultExecutionOrder(32000)]
    public sealed class RuntimeMovingSupport : MonoBehaviour
    {
        static RuntimeMovingSupport instance;
        GameObject player;
        CharacterController controller;
        PlayerMotor motor;
        PlayerGroundMotor ground;
        Collider support;
        MotionVisualView frame;
        Matrix4x4 previousInverse;
        Quaternion previousRotation;
        Vector3 previousCompensation;
        float savedFollower,appliedFollower;
        bool followerOverride;
        float nextLog;
        float nextPrune;
        readonly HashSet<MotionVisualView> frames=new HashSet<MotionVisualView>();

        internal static void Ensure()
        {
            if(instance!=null)return;
            var go=new GameObject("Runtime Moving Object Support");DontDestroyOnLoad(go);
            go.AddComponent<RuntimeMovingSupport>();
        }
        internal static void Register(MotionVisualView view)
        {
            Ensure();if(view!=null)instance.frames.Add(view);
        }
        bool Nearby()
        {
            if(Time.unscaledTime>=nextPrune){nextPrune=Time.unscaledTime+1;frames.RemoveWhere(view=>view==null);}
            foreach(var view in frames)if(view!=null&&view.CarryLocalPlayer&&view.gameObject.activeInHierarchy&&
                (view.transform.position-player.transform.position).sqrMagnitude<=32f*32f)return true;
            return false;
        }
        void Awake()
        {
            if(instance!=null&&instance!=this){Destroy(gameObject);return;}
            instance=this;MultiplayerAPI.SessionEnded+=ResetSupport;
        }
        void OnDestroy()
        {
            if(instance!=this)return;
            MultiplayerAPI.SessionEnded-=ResetSupport;ResetSupport();instance=null;
        }
        void OnDisable(){Release();}
        void ResetSupport(){Release();player=null;controller=null;motor=null;ground=null;}
        bool Ready()
        {
            if(!NetworkClient.active||!NetworkClient.ready||MultiplayerAPI.LocalPlayerId==0||
                !GameManager.HasInstance||SaveLoadManager.Instance==null||SaveLoadManager.Instance.LoadInProgress)return false;
            // A known supported passenger can remain aboard while simulation is
            // paused. A menu with no player never causes a GameManager.PlayerObject lookup.
            if(!GameManager.Instance.IsPlayingGame()&&(support==null||Time.timeScale>0.0001f))return false;
            if(player==null||!player.activeInHierarchy){
                Release();player=GameObject.FindGameObjectWithTag("Player");
                if(player==null||!player.activeInHierarchy)return false;
                controller=player.GetComponent<CharacterController>();motor=player.GetComponent<PlayerMotor>();ground=player.GetComponent<PlayerGroundMotor>();
            }
            return controller!=null&&controller.enabled&&controller.detectCollisions&&motor!=null&&
                !motor.IsLevitating&&!(motor.IsJumping&&motor.MoveDirection.y>0);
        }
        bool Eligible(Collider collider,out MotionVisualView view)
        {
            view=null;
            if(collider==null||!collider.enabled||collider.isTrigger||!collider.gameObject.activeInHierarchy)return false;
            view=collider.GetComponentInParent<MotionVisualView>();
            if(view==null||!view.CarryLocalPlayer||!view.gameObject.activeInHierarchy)return false;
            var tag=view.GetComponent<SyncedModObject>();
            return tag!=null&&tag.Replica&&tag.OwnerPlayerId!=0&&tag.OwnerPlayerId!=MultiplayerAPI.LocalPlayerId;
        }
        bool Probe(Vector3 foot,out RaycastHit hit)
        {
            return Physics.Raycast(foot+Vector3.up*.18f,Vector3.down,out hit,.40f,
                Physics.DefaultRaycastLayers&~(1<<player.layer),QueryTriggerInteraction.Ignore)&&hit.normal.y>.5f;
        }
        void CacheFrame(Collider floor,MotionVisualView view)
        {
            support=floor;frame=view;previousInverse=floor.transform.worldToLocalMatrix;
            previousRotation=floor.transform.rotation;
            previousCompensation=GameManager.Instance.StreamingWorld.WorldCompensation;
        }
        void BoostFollower()
        {
            if(!followerOverride){savedFollower=motor.smoothFollowerLerpSpeed;appliedFollower=Mathf.Max(savedFollower,250f);followerOverride=true;}
            motor.smoothFollowerLerpSpeed=appliedFollower;
        }
        void ClearNativePlatform()
        {
            if(ground!=null&&frame!=null&&ground.ActivePlatform!=null&&ground.ActivePlatform.IsChildOf(frame.transform))ground.ClearActivePlatform();
        }
        void Release()
        {
            ClearNativePlatform();
            if(followerOverride&&motor!=null&&Mathf.Abs(motor.smoothFollowerLerpSpeed-appliedFollower)<.01f)motor.smoothFollowerLerpSpeed=savedFollower;
            if(support!=null&&Time.unscaledTime>=nextLog){nextLog=Time.unscaledTime+1;Debug.Log("[ModObjectSync][Passenger] Detached from moving replica.");}
            support=null;frame=null;followerOverride=false;
        }
        void LateUpdate()
        {
            if(!Ready()){Release();return;}
            if(support==null&&followerOverride)Release();
            // No global transform sync or physics probe when no registered platform
            // is nearby. The registry contains only opted-in replica views.
            if(support==null&&!Nearby())return;
            Physics.SyncTransforms();
            var bounds=controller.bounds;var foot=new Vector3(bounds.center.x,bounds.min.y,bounds.center.z);
            MotionVisualView candidate;RaycastHit hit;
            if(support!=null){
                if(!Eligible(support,out candidate)||candidate!=frame){Release();return;}
                // Apply only the platform's change since the last rendered frame.
                // Walking performed by PlayerMotor in the meantime is preserved.
                var shift=GameManager.Instance.StreamingWorld.WorldCompensation-previousCompensation;
                var point=support.transform.TransformPoint(previousInverse.MultiplyPoint3x4(player.transform.position-shift));
                var projectedFoot=support.transform.TransformPoint(previousInverse.MultiplyPoint3x4(foot-shift));
                var delta=point-player.transform.position;
                if(delta.sqrMagnitude>frame.MaxPassengerStepMetres*frame.MaxPassengerStepMetres||
                    !Probe(projectedFoot,out hit)||!Eligible(hit.collider,out candidate)||candidate!=frame){Release();return;}
                ClearNativePlatform(); // Do not apply the same movement again in FixedUpdate.
                player.transform.position=point;
                var rotation=support.transform.rotation*Quaternion.Inverse(previousRotation);
                rotation=Quaternion.FromToRotation(rotation*player.transform.up,player.transform.up)*rotation;
                player.transform.rotation=rotation*player.transform.rotation;
                Physics.SyncTransforms();BoostFollower();CacheFrame(hit.collider,candidate);
            }else if(Probe(foot,out hit)&&Eligible(hit.collider,out candidate)){
                CacheFrame(hit.collider,candidate);ClearNativePlatform();BoostFollower();
                if(Time.unscaledTime>=nextLog){nextLog=Time.unscaledTime+1;Debug.Log("[ModObjectSync][Passenger] Attached to moving replica.");}
            }
        }
    }
}
