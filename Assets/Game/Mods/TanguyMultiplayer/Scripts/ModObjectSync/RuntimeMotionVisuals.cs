// Optional generic presentation layered on basic snapshots. No mod types/assets here.
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using UnityEngine;
using DaggerfallCoop.API;

namespace DaggerfallCoop.ObjectSync
{
    [Serializable] public sealed class MotionVisualPacket
    {
        public int Version = 1;
        public string Basic;
        public MotionSpriteSample[] Sprites = new MotionSpriteSample[0];
    }
    // Array entries are value types; absent sprite data means an empty array.
    [Serializable] public struct MotionSpriteSample
    {
        public int Node, Direction, Frame;
        public bool Flipped;
        public float Angle;
        public string Catalog;
    }
    [Serializable] public struct MotionSpriteImage { public int Direction, Frame; public string Texture; }
    [Serializable] public sealed class MotionSpriteCatalog
    {
        public int Directions, AngleSign;
        public MotionSpriteImage[] Images = new MotionSpriteImage[0];
    }

    public sealed class RuntimeMotionVisuals : MonoBehaviour
    {
        public const string FactoryId = "dfucoop.runtime-motion-visual.v1";
        static RuntimeMotionVisuals instance;
        sealed class SourceCatalog { public float Next; public string Key; public int Count; }
        sealed class SourceTopology { public Transform[] Nodes; public int[] Materials; }
        readonly Dictionary<int,SourceTopology> topologies = new Dictionary<int,SourceTopology>();
        readonly Dictionary<int,SourceCatalog> sourceCatalogs = new Dictionary<int,SourceCatalog>();
        readonly Dictionary<string,MotionSpriteCatalog> catalogs = new Dictionary<string,MotionSpriteCatalog>();
        readonly HashSet<string> warnings = new HashSet<string>();
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if(instance!=null)return;
            var go=new GameObject("Runtime Motion Visuals");DontDestroyOnLoad(go);go.AddComponent<RuntimeMotionVisuals>();
        }
        void Awake()
        {
            if(instance!=null&&instance!=this){Destroy(gameObject);return;}
            instance=this;
            GenericModObjectSync.RegisterConfiguredFactory(FactoryId,Capture,Build);
            MultiplayerAPI.SessionEnded+=Clear;
        }
        void Clear(){sourceCatalogs.Clear();catalogs.Clear();topologies.Clear();warnings.Clear();}
        void OnDestroy(){if(instance==this){MultiplayerAPI.SessionEnded-=Clear;Clear();instance=null;}}
        void Warn(string key,Exception error)
        {
            if(warnings.Count<128&&warnings.Add(key))Debug.LogWarning("[ModObjectSync][MotionVisual] "+key+": "+error.Message+"; retaining basic visual.");
        }
        static bool Finite(float value){return !float.IsNaN(value)&&!float.IsInfinity(value);}
        static int Wrap(int value,int count){return (value%count+count)%count;}
        internal static float ViewAngle(Transform root,Vector3 camera)
        {
            var delta=camera-root.position;var forward=root.forward;
            return (float)((Math.Atan2(delta.x,delta.z)-Math.Atan2(forward.x,forward.z))*180/Math.PI);
        }
        static Regex Pattern(ObjectSyncRule rule)
        {
            if(rule.SpriteDirections<2||rule.SpriteDirections>16||Math.Abs(rule.SpriteAngleSign)!=1||
                string.IsNullOrEmpty(rule.SpriteTexturePattern)||rule.SpriteTexturePattern.Length>200||
                rule.SpriteTextureFields==null||rule.SpriteTextureFields.Length>4)throw new InvalidDataException("Invalid sprite configuration");
            return new Regex(rule.SpriteTexturePattern,RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(25));
        }
        static bool Parse(Regex pattern,string name,out int direction,out int frame)
        {
            direction=frame=0;var match=pattern.Match(name??"");
            return match.Success&&int.TryParse(match.Groups["direction"].Value,out direction)&&int.TryParse(match.Groups["frame"].Value,out frame);
        }
        static int IntegerField(Component component,string field,int fallback)
        {
            var value=RuntimeObjectFields.Field(component,field);
            try{return value==null?fallback:Convert.ToInt32(value);}catch{return fallback;}
        }
        string CaptureCatalog(Component component,ObjectSyncRule rule,Regex pattern)
        {
            SourceCatalog entry;
            if(!sourceCatalogs.TryGetValue(component.GetInstanceID(),out entry)){
                if(sourceCatalogs.Count>=256)throw new InvalidDataException("Sprite source limit reached");
                sourceCatalogs.Add(component.GetInstanceID(),entry=new SourceCatalog());
            }
            if(Time.unscaledTime<entry.Next)return entry.Key;
            entry.Next=Time.unscaledTime+1;
            var images=new List<MotionSpriteImage>();var seen=new HashSet<int>();var keys=new HashSet<string>();
            foreach(string field in rule.SpriteTextureFields){
                if(string.IsNullOrEmpty(field)||field.Length>128)throw new InvalidDataException("Invalid sprite texture field");
                foreach(var reference in RuntimeObjectFields.Read(RuntimeObjectFields.Field(component,field))){
                    var image=reference.Value as Texture2D;if(image==null||!seen.Add(image.GetInstanceID()))continue;
                    int direction,frame;
                    if(!Parse(pattern,image.name,out direction,out frame)||direction<0||direction>=rule.SpriteDirections||frame<0||frame>63||!keys.Add(direction+":"+frame))continue;
                    if(images.Count>=256)throw new InvalidDataException("Sprite image limit reached");
                    images.Add(new MotionSpriteImage{Direction=direction,Frame=frame,Texture=RuntimeVisualSnapshot.Service.CapturePresentationTexture(image)});
                }
            }
            if(images.Count==0){Warn(rule.Id+"/sprite-assets",new InvalidDataException("No configured directional textures found"));return entry.Key;}
            images.Sort((a,b)=>a.Direction==b.Direction?a.Frame.CompareTo(b.Frame):a.Direction.CompareTo(b.Direction));
            var catalog=new MotionSpriteCatalog{Directions=rule.SpriteDirections,AngleSign=rule.SpriteAngleSign,Images=images.ToArray()};
            byte[] json=Encoding.UTF8.GetBytes(JsonUtility.ToJson(catalog));var bytes=new byte[json.Length+1];bytes[0]=3;Array.Copy(json,0,bytes,1,json.Length);
            string key=RuntimeVisualSnapshot.Service.StorePresentationAsset(bytes);
            if(entry.Key!=key){var directions=new HashSet<int>();foreach(var image in images)directions.Add(image.Direction);
                Debug.Log("[ModObjectSync][MotionVisual] Sprite catalogue images="+images.Count+" directions="+directions.Count+" source='"+component.gameObject.name+"'.");}
            entry.Key=key;entry.Count=images.Count;return key;
        }
        string Capture(GameObject source,ObjectSyncRule rule)
        {
            var currentNodes=source.GetComponentsInChildren<Transform>(true);
            if(currentNodes.Length==0||currentNodes.Length>48)throw new InvalidDataException("Motion visual exceeds 48 transforms");
            var materialCounts=new int[currentNodes.Length];
            for(int i=0;i<currentNodes.Length;i++){var renderer=currentNodes[i].GetComponent<MeshRenderer>();materialCounts[i]=renderer==null?0:renderer.sharedMaterials.Length;}
            SourceTopology previous;bool changed=false;
            if(topologies.TryGetValue(source.GetInstanceID(),out previous)){
                changed=previous.Nodes.Length!=currentNodes.Length;
                if(!changed)for(int i=0;i<currentNodes.Length;i++)if(previous.Nodes[i]!=currentNodes[i]||previous.Materials[i]!=materialCounts[i]){changed=true;break;}
            }else if(topologies.Count>=256)throw new InvalidDataException("Motion source limit reached");
            topologies[source.GetInstanceID()]=new SourceTopology{Nodes=currentNodes,Materials=materialCounts};
            if(changed){
                RuntimeVisualSnapshot.Service.ForgetBasicLayout(source);
                // Existing bindings cache their hierarchy; a new source record is safer
                // than inserting uncached colliders/renderer parts into that binding.
                throw new InvalidDataException("Motion hierarchy changed; re-enrolling its current geometry");
            }
            // Capture the known working appearance first. Optional sprite metadata
            // must never make a wagon or a map fail the basic factory's decoder.
            string basic=RuntimeVisualSnapshot.Service.CaptureBasic(source);
            var samples=new List<MotionSpriteSample>();
            if(rule!=null&&!string.IsNullOrEmpty(rule.SpriteComponentType)){
                try{
                    var pattern=Pattern(rule);var nodes=currentNodes;
                    for(int i=0;i<nodes.Length;i++)foreach(var component in nodes[i].GetComponents<Component>()){
                        if(component==null||component.GetType().FullName!=rule.SpriteComponentType)continue;
                        string catalog=CaptureCatalog(component,rule,pattern);if(string.IsNullOrEmpty(catalog))continue;
                        var renderer=nodes[i].GetComponent<MeshRenderer>();var material=renderer==null?null:renderer.sharedMaterial;
                        int direction,frame;
                        bool parsed=Parse(pattern,material==null||material.mainTexture==null?null:material.mainTexture.name,out direction,out frame);
                        int nativeDirection=IntegerField(component,rule.SpriteDirectionField,-1);
                        if(nativeDirection>=0&&nativeDirection<rule.SpriteDirections)direction=nativeDirection;
                        else if(!parsed)direction=0;
                        if(!parsed)frame=IntegerField(component,rule.SpriteFrameField,0);
                        var value=RuntimeObjectFields.Field(component,rule.SpriteFlipField);bool flip=value is bool&&(bool)value;
                        if(flip&&direction>0&&direction<rule.SpriteDirections/2)direction=rule.SpriteDirections-direction;
                        samples.Add(new MotionSpriteSample{Node=i,Catalog=catalog,Direction=Wrap(direction,rule.SpriteDirections),Frame=Math.Max(0,Math.Min(63,frame)),
                            Flipped=flip,Angle=Camera.main==null?0:ViewAngle(source.transform,Camera.main.transform.position)});
                    }
                }catch(Exception error){Warn(rule.Id+"/sprite",error);}
            }
            return JsonUtility.ToJson(new MotionVisualPacket{Basic=basic,Sprites=samples.ToArray()});
        }
        internal MotionSpriteCatalog ReadCatalog(string key)
        {
            if(string.IsNullOrEmpty(key))return null;
            MotionSpriteCatalog catalog;if(catalogs.TryGetValue(key,out catalog))return catalog;
            var blob=RuntimeVisualSnapshot.Service.ReadPresentationAsset(key,3);if(blob==null)return null;
            catalog=JsonUtility.FromJson<MotionSpriteCatalog>(Encoding.UTF8.GetString(blob,1,blob.Length-1));
            if(catalog==null||catalog.Directions<2||catalog.Directions>16||Math.Abs(catalog.AngleSign)!=1||catalog.Images==null||catalog.Images.Length>256)
                throw new InvalidDataException("Invalid sprite catalogue");
            foreach(var image in catalog.Images)if(image.Direction<0||image.Direction>=catalog.Directions||image.Frame<0||image.Frame>63||string.IsNullOrEmpty(image.Texture)||image.Texture.Length!=32)
                throw new InvalidDataException("Invalid sprite image");
            if(catalogs.Count>=512)throw new InvalidDataException("Sprite catalogue cache full");
            catalogs.Add(key,catalog);return catalog;
        }
        internal Texture2D Image(string key){return RuntimeVisualSnapshot.Service.LoadPresentationTexture(key);}
        GameObject Build(ObjectSyncVisual visual,GameObject existing)
        {
            var packet=JsonUtility.FromJson<MotionVisualPacket>(visual.CustomData);
            if(packet==null||packet.Version!=1||string.IsNullOrEmpty(packet.Basic)||packet.Basic.Length>6500||packet.Sprites!=null&&packet.Sprites.Length>48)
                throw new InvalidDataException("Invalid motion presentation packet");
            var previous=existing==null?null:existing.GetComponent<MotionVisualView>();
            if(previous!=null)previous.Prepare();
            var basic=new ObjectSyncVisual{Name=visual.Name,FactoryId=RuntimeVisualSnapshot.FactoryId,CustomData=packet.Basic,InstalledMod=visual.InstalledMod};
            var root=RuntimeVisualSnapshot.Service.BuildBasic(basic,existing);if(root==null)return null;
            var view=root.GetComponent<MotionVisualView>();bool fresh=view==null;
            if(fresh)view=root.AddComponent<MotionVisualView>();
            view.Accept(root.GetComponent<RuntimeVisualView>(),visual.LocalRule,visual.SmoothPresentation,packet,ReadCatalog,Image,fresh);
            return root;
        }
    }

    [DefaultExecutionOrder(31000)]
    public sealed class MotionVisualView : MonoBehaviour
    {
        [NonSerialized] public RuntimeVisualView Basic;
        [NonSerialized] public GameObject[] Collision;
        [NonSerialized] public Vector3[] From,To;
        [NonSerialized] public Quaternion[] FromRotation,ToRotation;
        [NonSerialized] public Vector3[] Display;
        [NonSerialized] public Quaternion[] DisplayRotation;
        [NonSerialized] public MotionSpriteSample[] Sprites;
        [NonSerialized] public bool Smooth;
        [NonSerialized] public bool CarryLocalPlayer;
        [NonSerialized] public float MaxPassengerStepMetres;
        float changedAt,window=.15f;
        string lastBasic;
        Vector2[] tiling,offset;
        Func<string,MotionSpriteCatalog> catalog;
        Func<string,Texture2D> image;
        readonly HashSet<string> warnings=new HashSet<string>();
        ModWorldPose lastPose;
        bool rootReady;
        Vector3 rootFrom,rootTo,lastCompensation;
        Quaternion rootFromRotation,rootToRotation;
        float rootChanged;
        public void Prepare()
        {
            if(Basic==null)return;
            AdvanceChildren();
            for(int i=0;i<Basic.Nodes.Length;i++){Display[i]=Basic.Nodes[i].localPosition;DisplayRotation[i]=Basic.Nodes[i].localRotation;}
        }
        public void Accept(RuntimeVisualView basic,ObjectSyncRule rule,bool smooth,MotionVisualPacket packet,
            Func<string,MotionSpriteCatalog> getCatalog,Func<string,Texture2D> getImage,bool fresh)
        {
            Basic=basic;Smooth=smooth;window=rule == null ? .15f : rule.PresentationSmoothingSeconds;
            CarryLocalPlayer=rule!=null&&rule.CarryLocalPlayer;
            MaxPassengerStepMetres=rule==null?3f:rule.MaxPassengerStepMetres;
            if(CarryLocalPlayer)RuntimeMovingSupport.Register(this);
            catalog=getCatalog;image=getImage;Sprites=packet.Sprites??new MotionSpriteSample[0];
            int count=basic.Nodes.Length;
            if(fresh){
                From=new Vector3[count];To=new Vector3[count];FromRotation=new Quaternion[count];ToRotation=new Quaternion[count];Display=new Vector3[count];DisplayRotation=new Quaternion[count];
                Collision=new GameObject[count];tiling=new Vector2[count];offset=new Vector2[count];
                for(int i=0;i<count;i++){
                    var material=basic.Materials[i].Length==1?basic.Materials[i][0]:null;
                    bool textured=material!=null&&material.HasProperty("_MainTex");
                    tiling[i]=textured?material.GetTextureScale("_MainTex"):Vector2.one;
                    offset[i]=textured?material.GetTextureOffset("_MainTex"):Vector2.zero;
                    var mesh=basic.Nodes[i].GetComponent<MeshFilter>();
                    if(rule!=null&&rule.ColliderMode=="Mesh"&&!basic.Facing[i]&&basic.Renderers[i]!=null&&mesh!=null&&mesh.sharedMesh!=null){
                        var collision=new GameObject("MP_VisualCollision");collision.transform.SetParent(basic.Nodes[i],false);collision.layer=0;
                        collision.AddComponent<MeshCollider>().sharedMesh=mesh.sharedMesh;Collision[i]=collision;
                    }
                }
            }
            bool changed=lastBasic!=packet.Basic;
            for(int i=0;i<count;i++){
                if(fresh||!Smooth){From[i]=To[i]=basic.Nodes[i].localPosition;FromRotation[i]=ToRotation[i]=basic.Nodes[i].localRotation;}
                else if(changed){From[i]=Display[i];To[i]=basic.Nodes[i].localPosition;FromRotation[i]=DisplayRotation[i];ToRotation[i]=basic.Nodes[i].localRotation;}
                if(Collision[i]!=null){
                    var mesh=basic.Nodes[i].GetComponent<MeshFilter>().sharedMesh;var collider=Collision[i].GetComponent<MeshCollider>();
                    if(collider.sharedMesh!=mesh)collider.sharedMesh=mesh;
                    Collision[i].SetActive(!basic.Renderers[i].forceRenderingOff);
                }
            }
            if(fresh||changed)changedAt=Time.unscaledTime;
            lastBasic=packet.Basic;
            AdvanceChildren();RefreshSprites();
        }
        void AdvanceChildren()
        {
            if(Basic==null||From==null)return;
            float t=Smooth?Mathf.Clamp01((Time.unscaledTime-changedAt)/window):1;
            for(int i=0;i<Basic.Nodes.Length;i++)if(Basic.Nodes[i]!=null){
                Basic.Nodes[i].localPosition=Vector3.Lerp(From[i],To[i],t);
                if(!Basic.Facing[i])Basic.Nodes[i].localRotation=Quaternion.Slerp(FromRotation[i],ToRotation[i],t);
            }
        }
        internal void RefreshSprites()
        {
            if(Camera.main==null||Sprites==null||catalog==null||image==null||Basic==null)return;
            foreach(var sample in Sprites){
                try{
                    if(sample.Node<0||sample.Node>=Basic.Nodes.Length||Basic.Materials[sample.Node].Length!=1||!Finite(sample.Angle))continue;
                    var images=catalog(sample.Catalog);if(images==null)continue;
                    float delta=RuntimeMotionVisuals.ViewAngle(Basic.Nodes[0],Camera.main.transform.position)-sample.Angle;
                    while(delta>180)delta-=360;while(delta< -180)delta+=360;
                    int step=(int)Math.Round(images.AngleSign*delta/(360f/images.Directions),MidpointRounding.AwayFromZero);
                    int direction=(sample.Direction+step)%images.Directions;if(direction<0)direction+=images.Directions;
                    int mirrored=(images.Directions-direction)%images.Directions;
                    MotionSpriteImage chosen=new MotionSpriteImage();int score=int.MaxValue;bool flip=false;
                    foreach(var candidate in images.Images){
                        if(candidate.Direction!=direction&&candidate.Direction!=mirrored)continue;
                        int cost=Math.Abs(candidate.Frame-sample.Frame)*2+(candidate.Direction==direction?0:1);
                        if(cost<score){score=cost;chosen=candidate;flip=candidate.Direction!=direction;}
                    }
                    if(score==int.MaxValue)continue;
                    var texture=image(chosen.Texture);if(texture==null)continue;
                    var material=Basic.Materials[sample.Node][0];if(!material.HasProperty("_MainTex"))continue;
                    material.SetTexture("_MainTex",texture);
                    bool reverse=flip!=sample.Flipped;var scale=tiling[sample.Node];var shift=offset[sample.Node];
                    if(reverse){shift.x+=scale.x;scale.x=-scale.x;}
                    material.SetTextureScale("_MainTex",scale);material.SetTextureOffset("_MainTex",shift);
                }catch(Exception error){
                    if(warnings.Count<16&&warnings.Add(sample.Catalog??"missing"))Debug.LogWarning("[ModObjectSync][MotionVisual] Sprite presentation: "+error.Message+"; retaining basic visual.");
                }
            }
        }
        static bool Finite(float v){return !float.IsNaN(v)&&!float.IsInfinity(v)&&Math.Abs(v)<=720;}
        void AdvanceRoot()
        {
            if(!Smooth||Basic==null||!DaggerfallWorkshop.Game.GameManager.HasInstance||
                !DaggerfallWorkshop.Game.GameManager.Instance.IsPlayingGame()||
                DaggerfallWorkshop.Game.Serialization.SaveLoadManager.Instance==null||
                DaggerfallWorkshop.Game.Serialization.SaveLoadManager.Instance.LoadInProgress)return;
            var binding=GetComponent<ModWorldBinding>();if(binding==null||binding.Record==null||binding.Record.Pose==null)return;
            var pose=binding.Record.Pose;Vector3 target;Quaternion rotation;if(!pose.TryLocal(out target,out rotation))return;
            var compensation=DaggerfallWorkshop.Game.GameManager.Instance.StreamingWorld.WorldCompensation;
            if(rootReady&&pose.Area=="E"){var shift=compensation-lastCompensation;rootFrom+=shift;rootTo+=shift;}
            lastCompensation=compensation;
            float t=Mathf.Clamp01((Time.unscaledTime-rootChanged)/window);
            var displayed=Vector3.Lerp(rootFrom,rootTo,t);var turn=Quaternion.Slerp(rootFromRotation,rootToRotation,t);
            if(!rootReady||lastPose==null||lastPose.Area!=pose.Area||Vector3.Distance(target,rootTo)>20){
                rootFrom=rootTo=target;rootFromRotation=rootToRotation=rotation;rootChanged=Time.unscaledTime;rootReady=true;
                displayed=target;turn=rotation;
            }else if(pose!=lastPose&&((target-rootTo).sqrMagnitude>.000001f||Quaternion.Angle(rotation,rootToRotation)>.01f)){
                rootFrom=displayed;rootTo=target;rootFromRotation=turn;rootToRotation=rotation;rootChanged=Time.unscaledTime;
            }
            lastPose=pose;
            Basic.Nodes[0].SetPositionAndRotation(displayed,turn);
        }
        void LateUpdate(){AdvanceChildren();AdvanceRoot();RefreshSprites();}
    }
}
