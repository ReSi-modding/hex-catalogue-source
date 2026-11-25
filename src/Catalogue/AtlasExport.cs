using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Order;

namespace ScriptDesk.Catalogue;
internal sealed partial class CatalogueEngine {
    internal sealed record AtlasInput(string name,int ordinal,int light,long size,string modified,string sha256,string[] masters);
    internal sealed record AtlasEnvironment(string dataPath,string pluginsPath,string cccPath,string pluginsHash,string cccHash);
    internal sealed record AtlasLabelSource(string path,long size,string modified,string sha256);
    internal sealed record AtlasReceipt(bool ready,bool stale,bool sourceCurrent,string path,string root,string sha256,long bytes,string pluginSetSha256,AtlasEnvironment environment,AtlasInput[] requiredPlugins,AtlasLabelSource[]? stringSources=null,string? evidencePath=null,string? evidenceSha256=null,long evidenceBytes=0);
    static readonly HashSet<string> AtlasCore=new(new[]{"Skyrim.esm","Update.esm","Dawnguard.esm","HearthFires.esm","Dragonborn.esm"},StringComparer.OrdinalIgnoreCase);
    const int AtlasMaxFile=128*1024*1024;
    static string AtlasSetHash(IEnumerable<AtlasInput> inputs)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",inputs.Select(p=>p.ordinal+":"+p.name.ToLowerInvariant()+":"+p.light+":"+p.sha256))))).ToLowerInvariant();
    AtlasEnvironment AtlasEnv()=>new(dataPath,pluginsPath,cccPath,HashFile(pluginsPath),HashFile(cccPath));
    AtlasInput[] AtlasInputs(){
        if(!Directory.Exists(dataPath)||!File.Exists(pluginsPath))throw new FileNotFoundException("Select the Skyrim Data directory and active Plugins.txt first.");
        var order=LoadOrder.GetLoadOrderListings(GameRelease.SkyrimSE,pluginsPath,File.Exists(cccPath)?cccPath:null,dataPath).Where(x=>x.Enabled).Select(x=>x.ModKey).Distinct().ToArray();
        if(order.Length==0||order.Length>4350)throw new InvalidDataException("The active plugin list is empty or exceeds Skyrim's supported slots.");
        var inputs=new List<AtlasInput>();int full=0,light=0;
        for(int i=0;i<order.Length;i++){
            string name=order[i].FileName.String;if(Path.GetFileName(name)!=name)throw new InvalidDataException("Invalid active plugin filename.");
            string file=Path.Combine(dataPath,name);var before=new FileInfo(file);if(!before.Exists||(before.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("An active atlas source is missing or redirected: "+name);
            long size=before.Length;var modified=before.LastWriteTimeUtc;var header=AtlasBinary.Header(file,name);string hash=HashFile(file);before.Refresh();
            if(size!=before.Length||modified!=before.LastWriteTimeUtc)throw new IOException("An atlas source changed while fingerprinting: "+name);
            if(header.light)light++;else full++;if(full>254||light>4096)throw new InvalidDataException("Skyrim plugin slot limits are exceeded.");
            inputs.Add(new(name,i,header.light?1:0,size,modified.ToString("O"),hash,header.masters));
        }
        return inputs.ToArray();
    }
    static bool AtlasSameInputs(AtlasInput[] a,AtlasInput[] b)=>a.Length==b.Length&&a.Zip(b).All(p=>p.First.name.Equals(p.Second.name,StringComparison.OrdinalIgnoreCase)&&p.First.ordinal==p.Second.ordinal&&p.First.light==p.Second.light&&p.First.sha256==p.Second.sha256);
    void AtlasFence(IEnumerable<AtlasInput> inputs){foreach(var p in inputs){var f=new FileInfo(Path.Combine(dataPath,p.name));if(!f.Exists||f.Length!=p.size||f.LastWriteTimeUtc.ToString("O")!=p.modified)throw new IOException("Atlas source changed during verification: "+p.name);}}
    static void AtlasAtomic(string path,byte[] bytes){string temporary=path+"."+Guid.NewGuid().ToString("N")+".building";try{File.WriteAllBytes(temporary,bytes);File.Move(temporary,path,true);}finally{if(File.Exists(temporary))File.Delete(temporary);}}
    // Atlas refresh already streams progress to its owning stdio client. A
    // synchronized/read-locked shared progress.json must not abort a read-only
    // atlas scan. Indexing retains its existing progress-file behavior.
    internal static void AtlasProgress(string message,int completed,int total,int records)=>Console.Error.WriteLine(JsonSerializer.Serialize(new{message,completed,total,records,at=DateTime.UtcNow}));
    object AtlasStatus(JsonElement request){
        long requestStarted=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string receiptPath=Path.Combine(root,"atlas-receipt.json");if(!File.Exists(receiptPath))return new{ready=false,stale=true,sourceCurrent=false,root,note="Refresh the installed atlas to map this Skyrim installation."};
        if(new FileInfo(receiptPath).Length>2*1024*1024)throw new InvalidDataException("Atlas receipt exceeds its supported size.");
        var r=JsonSerializer.Deserialize<AtlasReceipt>(File.ReadAllBytes(receiptPath))??throw new InvalidDataException("Atlas receipt is unavailable.");
        string expected=Path.Combine(root,"atlas-"+r.sha256+".json");
        if(!System.Text.RegularExpressions.Regex.IsMatch(r.sha256,"^[a-f0-9]{64}$")||!Path.GetFullPath(r.path).Equals(Path.GetFullPath(expected),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Atlas cache identity is invalid.");
        var env=AtlasEnv();var inputs=AtlasInputs();_ = new AtlasOrder(inputs,request,requestStarted);bool exists=File.Exists(expected);long bytes=exists?new FileInfo(expected).Length:0;
        bool labels=true;foreach(var source in r.stringSources??Array.Empty<AtlasLabelSource>()){
            string file=Path.GetFullPath(source.path);if(!file.StartsWith(Path.GetFullPath(dataPath).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Invalid atlas label-source path.");
            var f=new FileInfo(file);if(!f.Exists||f.Length!=source.size||f.LastWriteTimeUtc.ToString("O")!=source.modified||HashFile(file)!=source.sha256)labels=false;
        }
        AtlasFence(inputs);var after=AtlasEnv();bool current=labels&&env==after&&env==r.environment&&AtlasSameInputs(inputs,r.requiredPlugins)&&AtlasSetHash(inputs)==r.pluginSetSha256;
        bool valid=exists&&bytes==r.bytes&&bytes<=AtlasMaxFile&&HashFile(expected)==r.sha256;
        return r with{ready=valid,stale=!(valid&&current),sourceCurrent=valid&&current,root=root};
    }
    object AtlasExport(JsonElement request){
#if HEX_TEST_BUILD
        try{return AtlasExportCore(request);}catch(Exception error){Console.Error.WriteLine("ATLAS_EXPORT_EXCEPTION "+error);throw;}
#else
        return AtlasExportCore(request);
#endif
    }
    static void AtlasReadStage(string stage,AtlasInput input,Action action){
#if HEX_TEST_BUILD
        try{action();}catch(Exception error){Console.Error.WriteLine("ATLAS_SOURCE_EXCEPTION stage="+stage+" plugin="+input.name+" ordinal="+input.ordinal+" "+error);throw;}
#else
        action();
#endif
    }
    object AtlasExportCore(JsonElement request){
        long requestStarted=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var exportLock=new FileStream(Path.Combine(root,"atlas-export.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var env=AtlasEnv();var inputs=AtlasInputs();var order=new AtlasOrder(inputs,request,requestStarted);var execution=order.Execution;var strings=new AtlasStrings(dataPath);var installed=new AtlasBinary.Snapshot();var baseline=new AtlasBinary.Snapshot();
        // Definition pass precedes placements: a late base override may turn an
        // earlier placed object into an ore source, or remove its mining script.
        var allOreKeys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for(int i=0;i<inputs.Length;i++){
            var p=execution[i];AtlasProgress("Reading atlas deposit definitions: "+p.name,i,inputs.Length,allOreKeys.Count);
            var scanner=new AtlasBinary(dataPath,p,strings);AtlasReadStage("definitions",p,()=>scanner.ReadDefinitions(installed,AtlasCore.Contains(p.name)?baseline:null,allOreKeys));
        }
        for(int pass=0;pass<2;pass++){
            if(pass==1){installed.ClearGeography();baseline.ClearGeography();}
            for(int i=0;i<inputs.Length;i++){
                var p=execution[i];AtlasProgress((pass==0?"Finding atlas reference identities: ":"Reading atlas terrain and places: ")+p.name,i,inputs.Length,installed.Refs.Count);
                var scanner=new AtlasBinary(dataPath,p,strings);AtlasReadStage(pass==0?"reference-identities":"geography",p,()=>{if(pass==0)scanner.ReadReferenceKeys(allOreKeys);else scanner.ReadGeography(installed,AtlasCore.Contains(p.name)?baseline:null,allOreKeys);});
            }
        }
        var coreEvidence=new Dictionary<string,object?>();var installedEvidence=new Dictionary<string,object?>();
        var packs=new[]{AtlasPack.Build(baseline,inputs.Where(p=>AtlasCore.Contains(p.name)).ToArray(),env,"base-game-dlc","Skyrim and DLC",strings,coreEvidence,order),AtlasPack.Build(installed,inputs,env,"installed-profile","Installed Skyrim profile",strings,installedEvidence,order)};
        byte[] evidence=JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=1,packs=new Dictionary<string,object?>{{"base-game-dlc",coreEvidence},{"installed-profile",installedEvidence}}});
        if(evidence.Length>128*1024*1024)throw new InvalidDataException("Atlas source evidence exceeds its supported size. Previous atlas preserved.");string evidenceHash=Convert.ToHexString(SHA256.HashData(evidence)).ToLowerInvariant();string evidencePath=Path.Combine(root,"atlas-evidence-"+evidenceHash+".json");
        var document=new{schemaVersion=2,id="universal-skyrim",label="Skyrim atlas",defaultPackId="base-game-dlc",sourceEvidenceSha256=evidenceHash,packs};
        byte[] json=JsonSerializer.SerializeToUtf8Bytes(document,new JsonSerializerOptions{WriteIndented=false,DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull});
        if(json.Length>AtlasMaxFile)throw new InvalidDataException("Installed atlas exceeds the supported 128 MiB cache size. Previous atlas preserved.");
        AtlasProgress("Checking atlas source fingerprints",inputs.Length,inputs.Length,installed.Refs.Count);
        var finalInputs=AtlasInputs();if(env!=AtlasEnv()||!AtlasSameInputs(inputs,finalInputs))throw new IOException("Installed atlas sources changed while reading. Previous atlas preserved.");
        strings.Validate();AtlasFence(inputs);if(env!=AtlasEnv())throw new IOException("Atlas listing changed during verification.");string hash=Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant();string path=Path.Combine(root,"atlas-"+hash+".json");
        AtlasAtomic(evidencePath,evidence);AtlasAtomic(path,json);var receipt=new AtlasReceipt(true,false,true,path,root,hash,json.LongLength,AtlasSetHash(inputs),env,inputs,strings.Inputs(),evidencePath,evidenceHash,evidence.LongLength);
        AtlasAtomic(Path.Combine(root,"atlas-receipt.json"),JsonSerializer.SerializeToUtf8Bytes(receipt));
        AtlasProgress("Atlas ready",inputs.Length,inputs.Length,installed.Refs.Count);return receipt;
    }
}
