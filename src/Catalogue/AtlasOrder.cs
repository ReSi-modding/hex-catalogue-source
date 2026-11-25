using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace ScriptDesk.Catalogue;
internal sealed partial class CatalogueEngine {
    internal sealed class AtlasOrder {
        readonly AtlasInput[] inputs;readonly Dictionary<string,int> indices;readonly ulong[][] reach;
        internal readonly List<string> Warnings=new();internal readonly AtlasInput[] Execution;
        internal bool Acyclic=true,RuntimePartitionsMatched;
        internal AtlasOrder(AtlasInput[] inputs,JsonElement request,long? requestStarted=null){
            this.inputs=inputs;indices=inputs.Select((p,i)=>(p.name,i)).ToDictionary(x=>x.name,x=>x.i,StringComparer.OrdinalIgnoreCase);int n=inputs.Length;reach=Enumerable.Range(0,n).Select(_=>new ulong[(n+63)/64]).ToArray();var direct=Enumerable.Range(0,n).Select(_=>new HashSet<int>()).ToArray();
            foreach(int kind in new[]{0,1}){var group=inputs.Where(p=>p.light==kind).ToArray();for(int i=1;i<group.Length;i++)direct[indices[group[i-1].name]].Add(indices[group[i].name]);}
            foreach(var p in inputs)foreach(string master in p.masters){if(indices.TryGetValue(master,out int m))direct[m].Add(indices[p.name]);else{Acyclic=false;Warnings.Add(p.name+": missing master "+master);}}
            if(request.ValueKind==JsonValueKind.Object&&request.TryGetProperty("runtimePlugins",out var runtime)&&runtime.ValueKind!=JsonValueKind.Null){
                if(!runtime.TryGetProperty("capturedAt",out var at)||!at.TryGetDouble(out double captured)||!double.IsFinite(captured))throw new InvalidDataException("Fresh runtime plugin identities are required.");
                double age=(requestStarted??DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())-captured;if(age< -1000||age>3000)throw new InvalidDataException("Runtime plugin identity expired. Wait for a fresh player update.");
                foreach(var kind in new[]{(name:"full",light:0),(name:"light",light:1)}){
                    if(!runtime.TryGetProperty(kind.name,out var array)||array.ValueKind!=JsonValueKind.Array||array.GetArrayLength()>4350)throw new InvalidDataException("Runtime plugin partition is unavailable.");
                    var names=array.EnumerateArray().Select(v=>v.ValueKind==JsonValueKind.String?v.GetString()!:throw new InvalidDataException("Invalid runtime plugin name.")).ToArray();
                    if(!names.SequenceEqual(inputs.Where(p=>p.light==kind.light).Select(p=>p.name),StringComparer.OrdinalIgnoreCase))throw new InvalidDataException("Installed and current runtime plugin identities differ. Select this client's Skyrim Data and plugin list first.");
                }
                RuntimePartitionsMatched=true;
            }
            // Stable Kahn order is only a candidate. A partial order may admit
            // other interleavings; every relevant winner is proved below.
            var order=new List<AtlasInput>();var indegree=new int[n];foreach(var targets in direct)foreach(int v in targets)indegree[v]++;var available=new SortedSet<int>(Enumerable.Range(0,n).Where(i=>indegree[i]==0));
            while(available.Count>0){int next=available.Min;available.Remove(next);order.Add(inputs[next]);foreach(int target in direct[next])if(--indegree[target]==0)available.Add(target);}
            if(order.Count!=n){Acyclic=false;Warnings.Add("Full/light order and master dependencies contain a cycle.");}
            Execution=Acyclic?order.ToArray():inputs;
            // Reverse-topological bitset unions avoid cubic closure work at the
            // engine's maximum 254 full + 4096 light plugin slots.
            if(Acyclic)foreach(var p in order.AsEnumerable().Reverse()){int u=indices[p.name];foreach(int v in direct[u]){reach[u][v>>6]|=1UL<<(v&63);for(int word=0;word<reach[u].Length;word++)reach[u][word]|=reach[v][word];}}
        }
        internal (bool verified,object proof) Prove(AtlasBinary.Snapshot snapshot){
            int checkedRecords=0,equalAmbiguities=0;var unproved=new List<object>();
            foreach(var family in new[]{snapshot.Worlds,snapshot.Cells,snapshot.Locations,snapshot.Bases,snapshot.Refs,snapshot.Lands})foreach(var r in family.Values){
                checkedRecords++;var versions=r.Versions;var maximal=versions.Where(v=>!versions.Any(w=>!v.plugin.Equals(w.plugin,StringComparison.OrdinalIgnoreCase)&&Follows(v.plugin,w.plugin))).ToArray();
                bool equal=maximal.Length>0&&maximal.Select(v=>v.semanticSha256).Distinct(StringComparer.Ordinal).Count()==1;
                if(!Acyclic||maximal.Length==0||maximal.Length>1&&!equal)unproved.Add(new{id=r.Key,type=r.Type,maximalPlugins=maximal.Select(v=>v.plugin).ToArray(),reason="Different semantic records are incomparable under full/light order and master dependencies."});else if(maximal.Length>1)equalAmbiguities++;
                if(maximal.Length==1&&!maximal[0].plugin.Equals(r.Plugin,StringComparison.OrdinalIgnoreCase))unproved.Add(new{id=r.Key,type=r.Type,reason="Selected record is not the unique dependency-constrained maximum."});
            }
            bool verified=Acyclic&&unproved.Count==0;
            return(verified,new{method="Declared master dependencies plus ordered full/light partitions; unique per-record maximal winner or exact decoded body, master table and parent context equality.",verified,acyclic=Acyclic,runtimePartitionsMatched=RuntimePartitionsMatched,checkedRecords,equalAmbiguities,unprovedRecordCount=unproved.Count,unprovedRecords=unproved,limitations="Runtime full/light partitions must still match fresh actual client identities before live guidance. No total cross-kind order is invented."});
        }
        bool Follows(string earlier,string later){int a=indices[earlier],b=indices[later];return(reach[a][b>>6]&(1UL<<(b&63)))!=0;}
        internal static string Semantic(byte[] decoded,string[] masters,string cell,string world,uint flags)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{masters,cell,world,flags=flags&~0x40000u,body=Convert.ToBase64String(decoded)})))).ToLowerInvariant();
    }
}
