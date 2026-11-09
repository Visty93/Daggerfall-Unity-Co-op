// Opt-in presentation snapshots for small procedural mod props. No mod-specific code.
// Only meshes, material appearance and transforms cross the network; no scripts/physics.
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEngine;
using Mirror;
using DaggerfallCoop.API;
using DaggerfallWorkshop;
using UnityEngine.Rendering;

namespace DaggerfallCoop.ObjectSync
{
    [Serializable] public sealed class RuntimeVisualDescription { public string Layout, State; }
    [Serializable] public sealed class RuntimeVisualLayout { public RuntimeVisualNode[] Nodes; }
    [Serializable] public sealed class RuntimeVisualNode
    {
        public string Name, LayerName;
        public int Parent, Layer;
        public bool FaceCamera;
        public RuntimeVisualMaterial[] Materials;
    }
    [Serializable] public sealed class RuntimeVisualMaterial
    {
        public string Shader;
        public string[] Keywords;
        public int RenderQueue = -1;
        public RuntimeVisualParameter[] Parameters;
        public Color Tint = Color.white;
        public Vector2 Tiling = Vector2.one, Offset;
    }
    [Serializable] public sealed class RuntimeVisualParameter { public string Name; public int Kind; }

    public sealed class RuntimeVisualSnapshot : MonoBehaviour
    {
        public const string FactoryId = "dfucoop.runtime-visual.v1";
        public const string MaterialFactoryId = "dfucoop.runtime-material-visual.v1";
        const string Address = "dfucoop.runtime-visual-assets.v1";
        const int ChunkSize = 12000, MaxAssetSize = 262144, MaxBytes = 4*1024*1024, MaxAssets = 512, MaxNodes = 48;
        static RuntimeVisualSnapshot instance;
        readonly Dictionary<string, Asset> assets = new Dictionary<string, Asset>();
        readonly Dictionary<string, Incoming> incoming = new Dictionary<string, Incoming>();
        readonly Dictionary<UnityEngine.Object, string> captured = new Dictionary<UnityEngine.Object, string>();
        readonly Dictionary<UnityEngine.Object, string> detailedCaptured = new Dictionary<UnityEngine.Object, string>();
        readonly Dictionary<int, SourceLayout> layouts = new Dictionary<int, SourceLayout>();
        readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();
        readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();
        readonly Dictionary<uint, int> pendingPerPeer = new Dictionary<uint, int>();
        readonly HashSet<string> serverStored = new HashSet<string>();
        int bytes, pendingBytes, decodedPixels;
        float nextUpload;
        sealed class Asset { public byte[] Data; public bool Local, Confirmed; public int Offset; public float RetryAt; }
        sealed class Incoming { public byte[] Data; public bool[] Parts; public int Count; }
        sealed class SourceLayout { public Transform[] Nodes; public RuntimeVisualLayout Layout; public string Key; public bool Detailed; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (instance != null) return;
            var go = new GameObject("Runtime Mod Visual Snapshots"); DontDestroyOnLoad(go);
            go.AddComponent<RuntimeVisualSnapshot>();
        }
        // Optional presentation extensions reuse the existing bounded asset transport.
        // No extra fields are added to the working basic/map layout or state formats.
        internal static RuntimeVisualSnapshot Service { get { if(instance==null)Boot();return instance; } }
        internal string CaptureBasic(GameObject source) { return Capture(source); }
        internal void ForgetBasicLayout(GameObject source) { layouts.Remove(source.GetInstanceID()); }
        internal GameObject BuildBasic(ObjectSyncVisual visual,GameObject existing) { return Build(visual,existing); }
        internal string StorePresentationAsset(byte[] data) { return Put(data); }
        internal byte[] ReadPresentationAsset(string key,byte kind) { return Get(key,kind); }
        internal string CapturePresentationTexture(Texture texture)
        {
            string key;
            if(texture!=null&&captured.TryGetValue(texture,out key))return key;
            // Optional view catalogues must leave room for new basic frames/props.
            if(assets.Count>=MaxAssets-64||bytes>=MaxBytes-1024*1024)
                throw new InvalidDataException("Optional sprite asset budget reached");
            return CaptureTexture(texture);
        }
        internal Texture2D LoadPresentationTexture(string key) { return LoadTexture(key); }
        void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;
            GenericModObjectSync.RegisterFactory(FactoryId, Capture, Build);
            GenericModObjectSync.RegisterFactory(MaterialFactoryId, CaptureDetailed, Build);
            MultiplayerAPI.RegisterServerHandler(Address, "asset", ReceiveUpload);
            MultiplayerAPI.RegisterClientHandler(Address, "ack", (context, data) => {
                if (data == null || data.Length != 16) return;
                Asset asset; if (assets.TryGetValue(Hex(data), out asset)) asset.Confirmed = true;
            });
            MultiplayerAPI.SubscribeState(Address, ReceiveState, snapshot => { });
            MultiplayerAPI.SessionEnded += Clear;
            Debug.Log("[ModObjectSync][RuntimeVisual] Enabled opt-in procedural visual replication.");
        }
        void OnDestroy()
        {
            if (instance != this) return;
            MultiplayerAPI.SessionEnded -= Clear; Clear();
            MultiplayerAPI.UnregisterServerHandler(Address, "asset");
            MultiplayerAPI.UnregisterClientHandler(Address, "ack"); MultiplayerAPI.UnsubscribeState(Address);
            instance = null;
        }
        void Clear()
        {
            foreach (var mesh in meshes.Values) Destroy(mesh);
            foreach (var texture in textures.Values) Destroy(texture);
            meshes.Clear(); textures.Clear(); assets.Clear(); incoming.Clear(); captured.Clear(); detailedCaptured.Clear(); layouts.Clear();
            pendingPerPeer.Clear(); serverStored.Clear(); bytes = pendingBytes = decodedPixels = 0; nextUpload = 0;
        }
        void Update()
        {
            if (!NetworkClient.active || !NetworkClient.ready || MultiplayerAPI.LocalPlayerId == 0 || Time.unscaledTime < nextUpload) return;
            nextUpload = Time.unscaledTime + .1f;
            int budget = 2; // At most 20 small upload chunks/sec, not a scene scan.
            foreach (var pair in assets)
            {
                var asset = pair.Value;
                if (!asset.Local || asset.Confirmed || Time.unscaledTime < asset.RetryAt) continue;
                if (asset.Offset >= asset.Data.Length) asset.Offset = 0;
                byte[] packet = Packet(pair.Key, asset.Data, asset.Offset);
                if (!MultiplayerAPI.SendToServer(Address, "asset", packet)) break;
                asset.Offset += Math.Min(ChunkSize, asset.Data.Length-asset.Offset);
                if (asset.Offset >= asset.Data.Length) asset.RetryAt = Time.unscaledTime + 4;
                if (--budget == 0) break;
            }
        }
        static string Hash(byte[] data)
        {
            using (var sha = SHA256.Create()) { var full = sha.ComputeHash(data); var id = new byte[16]; Array.Copy(full,id,16); return Hex(id); }
        }
        static string Hex(byte[] id) { return BitConverter.ToString(id).Replace("-", "").ToLowerInvariant(); }
        static bool ValidKey(string key)
        {
            if (key == null || key.Length != 32) return false;
            foreach (char c in key) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false;
            return true;
        }
        static byte[] Id(string key)
        {
            var id = new byte[16];
            if (string.IsNullOrEmpty(key)) return id;
            if (!ValidKey(key)) throw new InvalidDataException("Invalid runtime asset key.");
            for (int i=0;i<16;i++) id[i]=Convert.ToByte(key.Substring(i*2,2),16);
            return id;
        }
        static string ReadId(BinaryReader reader)
        {
            var id=reader.ReadBytes(16); if(id.Length!=16) throw new EndOfStreamException();
            bool empty=true; foreach(byte b in id) if(b!=0) empty=false;
            return empty?null:Hex(id);
        }
        string Put(byte[] data)
        {
            if (data == null || data.Length < 2 || data.Length > MaxAssetSize) throw new InvalidDataException("Runtime visual asset is too large.");
            string key = Hash(data); Asset asset;
            if (assets.TryGetValue(key,out asset)) { asset.Local=true; return key; }
            if (assets.Count >= MaxAssets || bytes+data.Length > MaxBytes) throw new InvalidDataException("Runtime visual session asset budget exceeded.");
            assets.Add(key,new Asset{Data=data,Local=true}); bytes+=data.Length; return key;
        }
        static byte[] Packet(string key, byte[] data, int offset)
        {
            using(var memory=new MemoryStream()) using(var writer=new BinaryWriter(memory)) {
                writer.Write(Id(key)); writer.Write(data.Length); writer.Write(offset);
                writer.Write(data,offset,Math.Min(ChunkSize,data.Length-offset)); return memory.ToArray();
            }
        }
        bool Assemble(byte[] packet, uint owner, out string key, out byte[] data)
        {
            key=null;data=null;
            if(packet==null || packet.Length<=24 || packet.Length>ChunkSize+24) return false;
            using(var memory=new MemoryStream(packet)) using(var reader=new BinaryReader(memory)) {
                key=ReadId(reader); int length=reader.ReadInt32(), offset=reader.ReadInt32();
                int count=packet.Length-24;
                if(key==null || length<2 || length>MaxAssetSize || offset<0 || offset%ChunkSize!=0 ||
                    offset>=length || count!=Math.Min(ChunkSize,length-offset)) return false;
                string transfer=owner+":"+key; Incoming item;
                if(!incoming.TryGetValue(transfer,out item)) {
                    int peerCount;pendingPerPeer.TryGetValue(owner,out peerCount);
                    if(peerCount>=16 || pendingBytes+length>MaxBytes) return false;
                    item=new Incoming{Data=new byte[length],Parts=new bool[(length+ChunkSize-1)/ChunkSize]};
                    incoming.Add(transfer,item);pendingPerPeer[owner]=peerCount+1;pendingBytes+=length;
                }
                if(item.Data.Length!=length) return false;
                int part=offset/ChunkSize;
                if(!item.Parts[part]) { Array.Copy(packet,24,item.Data,offset,count);item.Parts[part]=true;item.Count++; }
                if(item.Count!=item.Parts.Length) return false;
                incoming.Remove(transfer);pendingPerPeer[owner]--;pendingBytes-=length;
                if(Hash(item.Data)!=key || item.Data[0]<1 || item.Data[0]>4) return false;
                data=item.Data;return true;
            }
        }
        void ReceiveUpload(ModMessageContext context, byte[] packet)
        {
            if(!context.IsServer || context.SenderPlayerId==0) return;
            string key;byte[] data;
            if(!Assemble(packet,context.SenderPlayerId,out key,out data)) return;
            if(!serverStored.Contains(key)) {
                Asset old;
                if(!assets.TryGetValue(key,out old)) {
                    if(assets.Count>=MaxAssets || bytes+data.Length>MaxBytes) return;
                    assets.Add(key,new Asset{Data=data});bytes+=data.Length;
                }
                for(int offset=0;offset<data.Length;offset+=ChunkSize) {
                    ulong revision;
                    if(!MultiplayerAPI.SetState(Address,key+"."+(offset/ChunkSize),Packet(key,data,offset),0,ulong.MaxValue,out revision)) return;
                }
                serverStored.Add(key);
                Debug.Log("[ModObjectSync][RuntimeVisual] Retained asset "+key+" bytes="+data.Length+" from="+context.SenderPlayerId);
            }
            MultiplayerAPI.SendToPlayer(context.SenderPlayerId,Address,"ack",Id(key));
        }
        void ReceiveState(ModStateChange change)
        {
            if(change.Removed) return;
            string key;byte[] data;
            if(!Assemble(change.Record.Payload,0,out key,out data)) return;
            Asset old;
            if(assets.TryGetValue(key,out old)) { old.Confirmed=true;return; }
            if(assets.Count>=MaxAssets || bytes+data.Length>MaxBytes) return;
            assets.Add(key,new Asset{Data=data,Confirmed=true});bytes+=data.Length;
        }
        byte[] Get(string key, byte kind)
        {
            Asset asset;
            return ValidKey(key) && assets.TryGetValue(key,out asset) && asset.Data[0]==kind ? asset.Data:null;
        }
        static byte[] Encode(Action<BinaryWriter> write)
        { using(var memory=new MemoryStream()) { using(var writer=new BinaryWriter(memory)) { write(writer);return memory.ToArray(); } } }
        // Basic factory keeps the v0.9 mesh/state path; map-only channels live below.
        string CaptureMesh(Mesh mesh)
        {
            if(mesh==null) return null;
            // Four-vertex sprites sometimes flip by editing UVs on the same mesh instance.
            string known;if(mesh.vertexCount>4 && captured.TryGetValue(mesh,out known)) return known;
            if(!mesh.isReadable || mesh.vertexCount>4096 || mesh.subMeshCount>8) throw new InvalidDataException("Runtime mesh must be readable and small (4096 vertices, 8 submeshes).");
            var vertices=mesh.vertices;var normals=mesh.normals;var uv=mesh.uv;
            byte[] data=Encode(writer=>{
                writer.Write((byte)2);writer.Write(vertices.Length);writer.Write(mesh.subMeshCount);
                for(int i=0;i<vertices.Length;i++) {
                    WriteVector(writer,vertices[i]);WriteVector(writer,normals.Length==vertices.Length?normals[i]:Vector3.up);
                    Vector2 t=uv.Length==vertices.Length?uv[i]:Vector2.zero;writer.Write(t.x);writer.Write(t.y);
                }
                for(int s=0;s<mesh.subMeshCount;s++) { var triangles=mesh.GetTriangles(s);if(triangles.Length>24576) throw new InvalidDataException("Runtime mesh index limit exceeded.");writer.Write(triangles.Length);foreach(int index in triangles)writer.Write(index); }
            });
            known=Put(data);captured[mesh]=known;return known;
        }
        string CaptureDetailedMesh(Mesh mesh)
        {
            if(mesh==null) return null;
            // Four-vertex sprites sometimes flip by editing UVs on the same mesh instance.
            string known;if(mesh.vertexCount>4 && detailedCaptured.TryGetValue(mesh,out known)) return known;
            if(!mesh.isReadable || mesh.vertexCount>4096 || mesh.subMeshCount>8) throw new InvalidDataException("Runtime mesh must be readable and small (4096 vertices, 8 submeshes).");
            var vertices=mesh.vertices;var normals=mesh.normals;var uv=mesh.uv;
            var uv2=mesh.uv2;var colors=mesh.colors;var tangents=mesh.tangents;
            byte flags=(byte)((uv2.Length==vertices.Length?1:0)|(colors.Length==vertices.Length?2:0)|(tangents.Length==vertices.Length?4:0));
            byte[] data=Encode(writer=>{
                writer.Write((byte)4);writer.Write(vertices.Length);writer.Write(mesh.subMeshCount);writer.Write(flags);
                WriteVector(writer,mesh.bounds.center);WriteVector(writer,mesh.bounds.size);
                for(int i=0;i<vertices.Length;i++) {
                    WriteVector(writer,vertices[i]);WriteVector(writer,normals.Length==vertices.Length?normals[i]:Vector3.up);
                    Vector2 t=uv.Length==vertices.Length?uv[i]:Vector2.zero;writer.Write(t.x);writer.Write(t.y);
                    if((flags&1)!=0){writer.Write(uv2[i].x);writer.Write(uv2[i].y);}
                    if((flags&2)!=0){var c=colors[i];WriteVector4(writer,new Vector4(c.r,c.g,c.b,c.a));}
                    if((flags&4)!=0)WriteVector4(writer,tangents[i]);
                }
                for(int s=0;s<mesh.subMeshCount;s++) { var triangles=mesh.GetTriangles(s);if(triangles.Length>24576) throw new InvalidDataException("Runtime mesh index limit exceeded.");writer.Write(triangles.Length);foreach(int index in triangles)writer.Write(index); }
            });
            known=Put(data);detailedCaptured[mesh]=known;return known;
        }
        string CaptureTexture(Texture texture)
        {
            if(texture==null) return null;
            string key;if(captured.TryGetValue(texture,out key)) return key;
            if(texture.width<1 || texture.height<1 || texture.width>512 || texture.height>512) throw new InvalidDataException("Runtime texture exceeds 512x512 limit.");
            Texture2D readable=texture as Texture2D;Texture2D copy=null;byte[] png=null;
            try {
                // isReadable alone is insufficient: readable compressed/crunched bundle
                // textures can still be unsupported by EncodeToPNG and return null.
                if(readable!=null&&readable.isReadable&&(readable.format==TextureFormat.RGBA32||readable.format==TextureFormat.ARGB32||readable.format==TextureFormat.RGB24))
                    png=readable.EncodeToPNG();
                if(png==null||png.Length==0) {
                    var previous=RenderTexture.active;var target=RenderTexture.GetTemporary(texture.width,texture.height,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);
                    try {
                        Graphics.Blit(texture,target);RenderTexture.active=target;
                        copy=new Texture2D(texture.width,texture.height,TextureFormat.RGBA32,false,false);
                        copy.ReadPixels(new Rect(0,0,texture.width,texture.height),0,0);copy.Apply();png=copy.EncodeToPNG();
                    } finally { RenderTexture.active=previous;RenderTexture.ReleaseTemporary(target); }
                }
            } finally { if(copy!=null) Destroy(copy); }
            if(png==null||png.Length==0)throw new InvalidDataException("Could not encode texture '"+texture.name+"' after an RGBA readback.");
            key=Put(Encode(writer=>{writer.Write((byte)1);writer.Write(texture.width);writer.Write(texture.height);writer.Write((int)texture.filterMode);writer.Write((int)texture.wrapMode);writer.Write(png);}));
            captured.Add(texture,key);return key;
        }
        static RuntimeVisualParameter[] Parameters(Material material)
        {
            var result=new List<RuntimeVisualParameter>();var shader=material.shader;
            if(shader.GetPropertyCount()>64)throw new InvalidDataException("Runtime shader property count exceeds 64.");
            for(int i=0;i<shader.GetPropertyCount();i++){
                string name=shader.GetPropertyName(i);var type=shader.GetPropertyType(i);
                if(name=="_MainTex"||type==ShaderPropertyType.Texture)continue;
                if(result.Count>=32||string.IsNullOrEmpty(name)||name.Length>128)throw new InvalidDataException("Runtime shader parameter limit exceeded.");
                if(type==ShaderPropertyType.Float||type==ShaderPropertyType.Range)result.Add(new RuntimeVisualParameter{Name=name,Kind=0});
                else if(type==ShaderPropertyType.Vector)result.Add(new RuntimeVisualParameter{Name=name,Kind=1});
                else if(type==ShaderPropertyType.Color)result.Add(new RuntimeVisualParameter{Name=name,Kind=2});
            }
            return result.ToArray();
        }
        SourceLayout Layout(GameObject source, bool detailed)
        {
            SourceLayout previous;
            if(layouts.TryGetValue(source.GetInstanceID(),out previous)) {
                bool valid=true;foreach(var node in previous.Nodes) if(node==null)valid=false;
                if(valid && previous.Detailed==detailed) return previous;
            }
            var nodes=source.GetComponentsInChildren<Transform>(true);
            if(nodes.Length==0 || nodes.Length>MaxNodes) throw new InvalidDataException("Runtime visual supports up to 48 transforms.");
            var indexes=new Dictionary<Transform,int>();for(int i=0;i<nodes.Length;i++)indexes[nodes[i]]=i;
            var definitions=new RuntimeVisualNode[nodes.Length];
            for(int i=0;i<nodes.Length;i++) {
                var node=nodes[i];var renderer=node.GetComponent<MeshRenderer>();
                var materials=renderer==null?new Material[0]:renderer.sharedMaterials;
                if(materials.Length>8) throw new InvalidDataException("Runtime visual material slot limit exceeded.");
                var defs=new RuntimeVisualMaterial[materials.Length];
                for(int m=0;m<defs.Length;m++) {
                    var material=materials[m];if(material==null || material.shader==null)throw new InvalidDataException("Runtime material is missing.");
                    defs[m]=new RuntimeVisualMaterial{Shader=material.shader.name,Keywords=detailed?material.shaderKeywords:new string[0],RenderQueue=detailed?material.renderQueue:-1,
                        Parameters=detailed?Parameters(material):new RuntimeVisualParameter[0],Tint=material.HasProperty("_Color")?material.GetColor("_Color"):Color.white,
                        Tiling=material.HasProperty("_MainTex")?material.GetTextureScale("_MainTex"):Vector2.one,Offset=material.HasProperty("_MainTex")?material.GetTextureOffset("_MainTex"):Vector2.zero};
                }
                definitions[i]=new RuntimeVisualNode{Name=node.name,Parent=i==0?-1:indexes[node.parent],Layer=detailed?node.gameObject.layer:0,LayerName=detailed?LayerMask.LayerToName(node.gameObject.layer):"",
                    FaceCamera=node.GetComponent<DaggerfallBillboard>()!=null,Materials=defs};
            }
            var layout=new RuntimeVisualLayout{Nodes=definitions};
            string key=Put(Encode(writer=>{writer.Write((byte)3);writer.Write(Encoding.UTF8.GetBytes(JsonUtility.ToJson(layout)));}));
            previous=new SourceLayout{Nodes=nodes,Layout=layout,Key=key,Detailed=detailed};layouts[source.GetInstanceID()]=previous;return previous;
        }
        string Capture(GameObject source)
        { return CaptureState(source,false); }
        string CaptureDetailed(GameObject source)
        { return CaptureState(source,true); }
        string CaptureState(GameObject source,bool detailed)
        {
            var layout=Layout(source,detailed);
            byte[] state=Encode(writer=>{
                writer.Write(detailed?(byte)3:(byte)2);writer.Write((byte)layout.Nodes.Length);
                for(int i=0;i<layout.Nodes.Length;i++) {
                    var node=layout.Nodes[i];var renderer=node.GetComponent<MeshRenderer>();
                    var materials=renderer==null?new Material[0]:renderer.sharedMaterials;
                    if(materials.Length!=layout.Layout.Nodes[i].Materials.Length)throw new InvalidDataException("Runtime material topology changed; recreate the source.");
                    writer.Write((byte)((node.gameObject.activeSelf?1:0)|(renderer!=null&&GenericModObjectSync.GetSourceRendererEnabled(renderer)?2:0)));
                    var filter=node.GetComponent<MeshFilter>();writer.Write(Id(filter==null?null:(detailed?CaptureDetailedMesh(filter.sharedMesh):CaptureMesh(filter.sharedMesh))));
                    WriteVector(writer,i==0?Vector3.zero:node.localPosition);
                    WriteRotation(writer,i==0?Quaternion.identity:node.localRotation);
                    WriteVector(writer,i==0?Vector3.one:node.localScale);
                    for(int m=0;m<materials.Length;m++){
                        var material=materials[m];writer.Write(Id(material!=null&&material.HasProperty("_MainTex")?CaptureTexture(material.GetTexture("_MainTex")):null));
                        var parameters=layout.Layout.Nodes[i].Materials[m].Parameters;
                        if(parameters!=null)foreach(var parameter in parameters){
                            if(parameter.Kind==0)writer.Write(material.GetFloat(parameter.Name));
                            else if(parameter.Kind==1)WriteVector4(writer,material.GetVector(parameter.Name));
                            else {var color=material.GetColor(parameter.Name);WriteVector4(writer,new Vector4(color.r,color.g,color.b,color.a));}
                        }
                    }
                }
            });
            return JsonUtility.ToJson(new RuntimeVisualDescription{Layout=layout.Key,State=Convert.ToBase64String(state)});
        }
        static void WriteVector(BinaryWriter w,Vector3 v){w.Write(v.x);w.Write(v.y);w.Write(v.z);}
        static void WriteRotation(BinaryWriter w,Quaternion q){w.Write(q.x);w.Write(q.y);w.Write(q.z);w.Write(q.w);}
        static void WriteVector4(BinaryWriter w,Vector4 v){w.Write(v.x);w.Write(v.y);w.Write(v.z);w.Write(v.w);}
        static float Number(BinaryReader r){float f=r.ReadSingle();if(float.IsNaN(f)||float.IsInfinity(f)||Math.Abs(f)>100000)throw new InvalidDataException("Invalid runtime geometry value.");return f;}
        static Vector3 ReadVector(BinaryReader r){return new Vector3(Number(r),Number(r),Number(r));}
        static Vector4 ReadVector4(BinaryReader r){return new Vector4(Number(r),Number(r),Number(r),Number(r));}
        static Quaternion ReadRotation(BinaryReader r){var q=new Quaternion(Number(r),Number(r),Number(r),Number(r));float length=q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w;if(length<.5f||length>1.5f)throw new InvalidDataException("Invalid runtime rotation.");return q;}
        Mesh LoadMesh(string key)
        {
            if(key==null)return null;Mesh mesh;if(meshes.TryGetValue(key,out mesh))return mesh;
            var data=Get(key,4)??Get(key,2);if(data==null)return null;
            using(var memory=new MemoryStream(data))using(var r=new BinaryReader(memory)) {
                byte kind=r.ReadByte();int count=r.ReadInt32(),submeshes=r.ReadInt32();if(count<1||count>4096||submeshes<1||submeshes>8)throw new InvalidDataException("Invalid runtime mesh counts.");
                byte flags=kind==4?r.ReadByte():(byte)0;if(flags>7)throw new InvalidDataException("Invalid runtime mesh channels.");
                Bounds bounds=new Bounds();if(kind==4){var center=ReadVector(r);var size=ReadVector(r);if(size.x<0||size.y<0||size.z<0)throw new InvalidDataException("Invalid runtime mesh bounds.");bounds=new Bounds(center,size);}
                var vertices=new Vector3[count];var normals=new Vector3[count];var uv=new Vector2[count];
                var uv2=(flags&1)!=0?new Vector2[count]:null;var colors=(flags&2)!=0?new Color[count]:null;var tangents=(flags&4)!=0?new Vector4[count]:null;
                for(int i=0;i<count;i++){
                    vertices[i]=ReadVector(r);normals[i]=ReadVector(r);uv[i]=new Vector2(Number(r),Number(r));
                    if(uv2!=null)uv2[i]=new Vector2(Number(r),Number(r));
                    if(colors!=null){var c=ReadVector4(r);colors[i]=new Color(c.x,c.y,c.z,c.w);}
                    if(tangents!=null)tangents[i]=ReadVector4(r);
                }
                var indices=new int[submeshes][];
                for(int s=0;s<submeshes;s++){int length=r.ReadInt32();if(length<0||length>24576||length%3!=0)throw new InvalidDataException("Invalid runtime triangles.");indices[s]=new int[length];for(int i=0;i<length;i++){int index=r.ReadInt32();if(index<0||index>=count)throw new InvalidDataException("Invalid runtime mesh index.");indices[s][i]=index;}}
                if(memory.Position!=memory.Length)throw new InvalidDataException("Invalid runtime mesh trailer.");
                mesh=new Mesh{name="MP_Runtime_"+key,vertices=vertices,normals=normals,uv=uv,subMeshCount=submeshes};
                if(uv2!=null)mesh.uv2=uv2;if(colors!=null)mesh.colors=colors;if(tangents!=null)mesh.tangents=tangents;
                for(int s=0;s<submeshes;s++)mesh.SetTriangles(indices[s],s);mesh.RecalculateBounds();if(kind==4)mesh.bounds=bounds;meshes.Add(key,mesh);return mesh;
            }
        }
        Texture2D LoadTexture(string key)
        {
            if(key==null)return null;Texture2D texture;if(textures.TryGetValue(key,out texture))return texture;
            var data=Get(key,1);if(data==null)return null;
            using(var memory=new MemoryStream(data))using(var r=new BinaryReader(memory)) {
                r.ReadByte();int width=r.ReadInt32(),height=r.ReadInt32(),filter=r.ReadInt32(),wrap=r.ReadInt32();
                var png=r.ReadBytes((int)(memory.Length-memory.Position));
                if(width<1||height<1||width>512||height>512||png.Length<24||png[0]!=137||png[1]!=80||png[2]!=78||png[3]!=71||
                    (png[16]<<24|png[17]<<16|png[18]<<8|png[19])!=width||(png[20]<<24|png[21]<<16|png[22]<<8|png[23])!=height||
                    !Enum.IsDefined(typeof(FilterMode),filter)||!Enum.IsDefined(typeof(TextureWrapMode),wrap))throw new InvalidDataException("Invalid runtime image header.");
                if(decodedPixels+width*height>16*1024*1024)throw new InvalidDataException("Runtime decoded image budget exceeded.");
                texture=new Texture2D(2,2,TextureFormat.RGBA32,false,false);
                if(!texture.LoadImage(png,true)){Destroy(texture);throw new InvalidDataException("Runtime image decode failed.");}
                texture.name="MP_Runtime_"+key;texture.filterMode=(FilterMode)filter;texture.wrapMode=(TextureWrapMode)wrap;
                textures.Add(key,texture);decodedPixels+=width*height;return texture;
            }
        }
        static GameObject WaitForAssets(GameObject existing)
        {
            if(existing!=null)throw new ObjectSyncPendingException();
            return null;
        }
        GameObject Build(ObjectSyncVisual description,GameObject existing)
        {
            bool detailed=description.FactoryId==MaterialFactoryId;
            var packet=JsonUtility.FromJson<RuntimeVisualDescription>(description.CustomData);
            if(packet==null||!ValidKey(packet.Layout)||packet.State==null||packet.State.Length>6500)return existing;
            byte[] definition=Get(packet.Layout,3);if(definition==null)return WaitForAssets(existing);
            var layout=JsonUtility.FromJson<RuntimeVisualLayout>(Encoding.UTF8.GetString(definition,1,definition.Length-1));
            if(layout==null||layout.Nodes==null||layout.Nodes.Length==0||layout.Nodes.Length>MaxNodes)throw new InvalidDataException("Invalid runtime hierarchy.");
            int count=layout.Nodes.Length;var positions=new Vector3[count];var rotations=new Quaternion[count];var scales=new Vector3[count];var flags=new byte[count];var images=new Texture2D[count][];var geometry=new Mesh[count];
            var parameterValues=new Vector4[count][][];var layers=new int[count];
            using(var memory=new MemoryStream(Convert.FromBase64String(packet.State)))using(var r=new BinaryReader(memory)) {
                byte stateVersion=r.ReadByte();if((stateVersion!=2&&stateVersion!=3)||r.ReadByte()!=count)throw new InvalidDataException("Runtime hierarchy/state mismatch.");
                for(int i=0;i<count;i++) {
                    var node=layout.Nodes[i];if(node==null||node.Parent>i-1||node.Parent< -1||i>0&&node.Parent<0||node.Name==null||node.Name.Length>128||node.Materials==null||node.Materials.Length>8)throw new InvalidDataException("Invalid runtime node.");
                    if(node.Layer<0||node.Layer>31||node.LayerName!=null&&node.LayerName.Length>64)throw new InvalidDataException("Invalid runtime render layer.");
                    layers[i]=string.IsNullOrEmpty(node.LayerName)?node.Layer:LayerMask.NameToLayer(node.LayerName);
                    if(layers[i]<0)throw new InvalidDataException("Required render layer is unavailable: "+node.LayerName);
                    flags[i]=r.ReadByte();string meshKey=ReadId(r);
                    if(meshKey!=null){geometry[i]=LoadMesh(meshKey);if(geometry[i]==null)return WaitForAssets(existing);}
                    positions[i]=ReadVector(r);rotations[i]=ReadRotation(r);scales[i]=ReadVector(r);
                    images[i]=new Texture2D[node.Materials.Length];
                    parameterValues[i]=new Vector4[node.Materials.Length][];
                    for(int m=0;m<images[i].Length;m++){
                        var info=node.Materials[m];if(info==null||info.Parameters!=null&&info.Parameters.Length>32)throw new InvalidDataException("Invalid runtime material parameters.");
                        var id=ReadId(r);if(id!=null){images[i][m]=LoadTexture(id);if(images[i][m]==null)return WaitForAssets(existing);}
                        var parameters=info.Parameters;parameterValues[i][m]=new Vector4[parameters==null?0:parameters.Length];
                        if(parameters!=null)for(int p=0;p<parameters.Length;p++){
                            var parameter=parameters[p];if(stateVersion<3||parameter==null||string.IsNullOrEmpty(parameter.Name)||parameter.Name.Length>128||parameter.Kind<0||parameter.Kind>2)throw new InvalidDataException("Invalid runtime shader parameter.");
                            parameterValues[i][m][p]=parameter.Kind==0?new Vector4(Number(r),0,0,0):ReadVector4(r);
                        }
                    }
                }
                if(memory.Position!=memory.Length)throw new InvalidDataException("Invalid runtime state trailer.");
            }
            var root=existing;var view=root==null?null:root.GetComponent<RuntimeVisualView>();
            if(root!=null&&(view==null||view.LayoutKey!=packet.Layout))throw new InvalidDataException("Runtime hierarchy changed; recreate the source.");
            if(root==null) {
                root=new GameObject("Runtime visual snapshot");view=root.AddComponent<RuntimeVisualView>();view.LayoutKey=packet.Layout;
                view.Nodes=new Transform[count];view.Renderers=new MeshRenderer[count];view.Materials=new Material[count][];view.Facing=new bool[count];
                try {
                    for(int i=0;i<count;i++) {
                        var def=layout.Nodes[i];var node=new GameObject("MP_Runtime_"+def.Name);view.Nodes[i]=node.transform;
                        node.layer=layers[i];
                        node.transform.SetParent(def.Parent<0?root.transform:view.Nodes[def.Parent],false);view.Facing[i]=def.FaceCamera;
                        view.Materials[i]=new Material[def.Materials.Length];
                        if(geometry[i]!=null)node.AddComponent<MeshFilter>().sharedMesh=geometry[i];
                        if(def.Materials.Length>0) {
                            for(int m=0;m<def.Materials.Length;m++) {
                                var info=def.Materials[m];if(info==null||info.Shader==null||info.Shader.Length>128)throw new InvalidDataException("Invalid runtime material.");
                                Shader shader=detailed?GenericModObjectSync.ResolveInstalledModShader(description.InstalledMod,info.Shader):Shader.Find(info.Shader);if(shader==null||!shader.isSupported)throw new InvalidDataException("Runtime shader unavailable: "+info.Shader);
                                var material=new Material(shader);view.Materials[i][m]=material;
                                if(detailed) {
                                if(info.RenderQueue< -1||info.RenderQueue>5000||info.Keywords!=null&&info.Keywords.Length>32)throw new InvalidDataException("Invalid runtime render settings.");
                                if(info.Keywords!=null){foreach(string keyword in info.Keywords)if(string.IsNullOrEmpty(keyword)||keyword.Length>128)throw new InvalidDataException("Invalid runtime shader keyword.");material.shaderKeywords=info.Keywords;}
                                material.renderQueue=info.RenderQueue;
                                }
                                if(material.HasProperty("_Color"))material.SetColor("_Color",info.Tint);
                                if(material.HasProperty("_MainTex")){material.SetTextureScale("_MainTex",info.Tiling);material.SetTextureOffset("_MainTex",info.Offset);}
                            }
                            view.Renderers[i]=node.AddComponent<MeshRenderer>();view.Renderers[i].sharedMaterials=view.Materials[i];
                        }
                    }
                } catch { Destroy(root);throw; }
                Debug.Log("[ModObjectSync][RuntimeVisual] Created replica hierarchy nodes="+count+" source='"+description.Name+"'.");
            }
            for(int i=0;i<count;i++) {
                view.Nodes[i].localPosition=positions[i];if(!view.Facing[i])view.Nodes[i].localRotation=rotations[i];view.Nodes[i].localScale=scales[i];
                var filter=view.Nodes[i].GetComponent<MeshFilter>();if(filter!=null)filter.sharedMesh=geometry[i];
                view.Nodes[i].gameObject.SetActive((flags[i]&1)!=0);
                // Active child objects hide optional parts. Keep renderer flags with their own
                // culling-independent child gate so ModWorld's root culling cannot undo them.
                if(view.Renderers[i]!=null) {
                    view.Renderers[i].forceRenderingOff=(flags[i]&2)==0;
                    for(int m=0;m<images[i].Length;m++){
                        var material=view.Materials[i][m];if(material.HasProperty("_MainTex"))material.SetTexture("_MainTex",images[i][m]);
                        var parameters=layout.Nodes[i].Materials[m].Parameters;
                        if(parameters!=null)for(int p=0;p<parameters.Length;p++){
                            var parameter=parameters[p];var value=parameterValues[i][m][p];
                            if(!material.HasProperty(parameter.Name))throw new InvalidDataException("Runtime shader parameter unavailable: "+parameter.Name);
                            if(parameter.Kind==0)material.SetFloat(parameter.Name,value.x);
                            else if(parameter.Kind==1)material.SetVector(parameter.Name,value);
                            else material.SetColor(parameter.Name,new Color(value.x,value.y,value.z,value.w));
                        }
                    }
                }
            }
            return root;
        }
    }
    public sealed class RuntimeVisualView : MonoBehaviour
    {
        public string LayoutKey;
        public Transform[] Nodes;public MeshRenderer[] Renderers;public Material[][] Materials;public bool[] Facing;
        void LateUpdate()
        {
            if(Camera.main==null||Nodes==null)return;
            var forward=Camera.main.transform.forward;var direction=Vector3.zero-new Vector3(forward.x,0,forward.z);
            if(direction.sqrMagnitude<.001f)return;
            for(int i=0;i<Nodes.Length;i++)if(Facing[i]&&Nodes[i]!=null&&Nodes[i].gameObject.activeInHierarchy)Nodes[i].LookAt(Nodes[i].position+direction);
        }
        void OnDestroy(){if(Materials!=null)foreach(var slots in Materials)if(slots!=null)foreach(var material in slots)if(material!=null)Destroy(material);}
    }
}
