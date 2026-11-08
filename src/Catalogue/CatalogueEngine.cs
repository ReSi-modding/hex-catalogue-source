using Microsoft.Data.Sqlite;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Archives;
using System.Text.Json;
using System.Security.Cryptography;

namespace ScriptDesk.Catalogue;
internal sealed partial class CatalogueEngine {
    internal const int Schema=4;
    internal string DatabasePath=>database;
    readonly string dataPath,pluginsPath,cccPath,database,root;
    internal CatalogueEngine(JsonElement request,string? testRoot=null){
        dataPath=Path.GetFullPath(Text(request,"dataPath",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),@"Steam\steamapps\common\Skyrim Special Edition\Data")));
        pluginsPath=Path.GetFullPath(Text(request,"pluginsPath",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"Skyrim Special Edition\Plugins.txt")));
        cccPath=Path.GetFullPath(Text(request,"cccPath",Path.Combine(Path.GetDirectoryName(dataPath)!,"Skyrim.ccc")));
        if(new[]{dataPath,pluginsPath,cccPath}.Any(p=>p.StartsWith(@"\\")))throw new InvalidDataException("Catalogue inputs must be on a local drive, not a UNC/network path.");
        var identity=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dataPath.ToUpperInvariant()+"\n"+pluginsPath.ToUpperInvariant())))[..16];
        root=Path.Combine(testRoot??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".script-desk-executor","catalogue"),identity);
        Directory.CreateDirectory(root);database=Path.Combine(root,"catalogue.sqlite");
    }
    internal object Execute(JsonElement r)=>Text(r,"command") switch {
        "index"=>Build(),"search"=>Search(r),"travelSearch"=>Search(r),"travelDestinations"=>TravelDestinations(r),"travelDoor"=>TravelDoor(r),"detail"=>Detail(Text(r,"key")),"sourceRecord"=>SourceRecord(Text(r,"key")),"placedReference"=>PlacedReference(r),"crossref"=>CrossReference(Text(r,"key")),"status"=>Status(),"assets"=>Assets(Text(r,"key")),"thumbnail"=>Thumbnail(Text(r,"key")),"modelVariants"=>ModelOptions(Text(r,"key")),"model"=>Model(Text(r,"key"),Text(r,"variant")),_=>throw new InvalidDataException("Unknown catalogue command.")};
    internal static string Text(JsonElement r,string key,string fallback="")=>r.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()!:fallback;
    static int Int(JsonElement r,string key,int fallback,int min,int max)=>r.TryGetProperty(key,out var v)&&v.TryGetInt32(out int n)?Math.Clamp(n,min,max):fallback;
    static SqliteConnection Open(string path,bool readOnly=false){var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=readOnly?SqliteOpenMode.ReadOnly:SqliteOpenMode.ReadWriteCreate,Pooling=false}.ToString());c.Open();return c;}
    internal static void Sql(SqliteConnection db,string sql,params object?[] values){using var cmd=db.CreateCommand();cmd.CommandText=sql;for(int i=0;i<values.Length;i++)cmd.Parameters.AddWithValue("$"+i,values[i]??DBNull.Value);cmd.ExecuteNonQuery();}
    static object? Scalar(SqliteConnection db,string sql){using var cmd=db.CreateCommand();cmd.CommandText=sql;return cmd.ExecuteScalar();}
    internal static List<Dictionary<string,object?>> Rows(SqliteConnection db,string sql,params object?[] values){
        using var cmd=db.CreateCommand();cmd.CommandText=sql;for(int i=0;i<values.Length;i++)cmd.Parameters.AddWithValue("$"+i,values[i]??DBNull.Value);
        using var reader=cmd.ExecuteReader();var rows=new List<Dictionary<string,object?>>();while(reader.Read()){var row=new Dictionary<string,object?>();for(int i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);rows.Add(row);}return rows;
    }
    internal static void CreateSchema(SqliteConnection db){Sql(db,"""
        PRAGMA journal_mode=DELETE;
        CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
        CREATE TABLE plugins(name TEXT COLLATE NOCASE PRIMARY KEY,ordinal INTEGER,light INTEGER,size INTEGER,modified TEXT,sha256 TEXT,masters TEXT);
        CREATE TABLE records(key TEXT COLLATE NOCASE PRIMARY KEY,name TEXT,editor TEXT,type TEXT,category TEXT,origin TEXT COLLATE NOCASE,winner TEXT COLLATE NOCASE,localId INTEGER,candidate TEXT,deleted INTEGER,playable INTEGER,enchanted INTEGER,weight REAL,value REAL,damage REAL,armor REAL,keywords TEXT,metadata TEXT);
        CREATE TABLE versions(key TEXT COLLATE NOCASE,plugin TEXT COLLATE NOCASE,ordinal INTEGER,deleted INTEGER,metadata TEXT,PRIMARY KEY(key,plugin));
        CREATE TABLE links(source TEXT COLLATE NOCASE,target TEXT COLLATE NOCASE,PRIMARY KEY(source,target));
        CREATE INDEX names ON records(name COLLATE NOCASE);CREATE INDEX categories ON records(category);CREATE INDEX winners ON records(winner);CREATE INDEX targets ON links(target);
        """);}
    object Build(){
        if(!Directory.Exists(dataPath)||!File.Exists(pluginsPath))throw new FileNotFoundException("Select the Skyrim Data directory and active Plugins.txt first.");
        using var buildLock=new FileStream(Path.Combine(root,"build.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        string initialPluginsHash=HashFile(pluginsPath),initialCccHash=HashFile(cccPath);
        string temporary=Path.Combine(root,"catalogue."+Guid.NewGuid().ToString("N")+".building");var indexed=new List<object>();var warnings=new List<string>();int total=0,full=0,light=0;
        var order=LoadOrder.GetLoadOrderListings(GameRelease.SkyrimSE,pluginsPath,File.Exists(cccPath)?cccPath:null,dataPath).Where(x=>x.Enabled).Select(x=>x.ModKey).Distinct().ToArray();
        var indices=new Dictionary<string,(int index,bool light)>(StringComparer.OrdinalIgnoreCase);
        try{
            using(var db=Open(temporary)){
                CreateSchema(db);
                for(int ordinal=0;ordinal<order.Length;ordinal++){
                    var key=order[ordinal];string path=Path.Combine(dataPath,key.FileName.String);if(!File.Exists(path))throw new FileNotFoundException("Active plugin missing: "+key);
                    var info=new FileInfo(path);var beforeWrite=info.LastWriteTimeUtc;var beforeSize=info.Length;Progress("Reading "+key,ordinal,order.Length,total);
                    using var mod=SkyrimMod.CreateFromBinaryOverlay(new ModPath(key,path),SkyrimRelease.SkyrimSE);
                    int slot=mod.IsSmallMaster?light++:full++;indices[key.ToString()]=(slot,mod.IsSmallMaster);
                    var masters=mod.ModHeader.MasterReferences.Select(m=>m.Master.ToString()).ToArray();
                    foreach(string master in masters)if(!indices.ContainsKey(master))warnings.Add(key+" lists master "+master+" before it appears in Plugins.txt. File order is not verified engine order.");
                    if(full>254||light>4096)throw new InvalidDataException("Load-order slot limit exceeded.");
                    using var tx=db.BeginTransaction();int count=0;
                    foreach(var recordType in RecordData.RecordTypes){
                        // A malformed group does not make unrelated categories unusable. Its scope is reported.
                        Sql(db,"SAVEPOINT category");int groupCount=0;
                        try{foreach(var record in mod.EnumerateMajorRecords(recordType)){
                            var model=RecordData.Read(record);if(model==null)continue;var original=record.FormKey.ModKey.ToString();string? candidate=null;
                            if(indices.TryGetValue(original,out var index))candidate=Candidate(record.FormKey.ID,index.index,index.light);
                            Insert(db,model,key.ToString(),ordinal,candidate);groupCount++;
                        }Sql(db,"RELEASE category");count+=groupCount;total+=groupCount;}
                        catch(Exception e){Sql(db,"ROLLBACK TO category");Sql(db,"RELEASE category");warnings.Add(key+" / "+recordType.Name+" excluded: "+e.GetBaseException().Message);}
                    }
                    info.Refresh();if(info.Length!=beforeSize||info.LastWriteTimeUtc!=beforeWrite)throw new IOException(key+" changed during indexing.");
                    string hash=HashFile(path);info.Refresh();if(info.Length!=beforeSize||info.LastWriteTimeUtc!=beforeWrite)throw new IOException(key+" changed while fingerprinting.");
                    Sql(db,"INSERT INTO plugins VALUES($0,$1,$2,$3,$4,$5,$6)",key.ToString(),ordinal,mod.IsSmallMaster?1:0,beforeSize,beforeWrite.ToString("O"),hash,JsonSerializer.Serialize(masters));tx.Commit();
                    indexed.Add(new{plugin=key.ToString(),records=count,light=mod.IsSmallMaster,slot});
                }
                if(warnings.Count>0)Sql(db,"UPDATE records SET candidate=NULL");
                Sql(db,"INSERT INTO meta VALUES('warnings',$0)",JsonSerializer.Serialize(warnings));
                Sql(db,"INSERT INTO meta VALUES('schema',$0)",Schema.ToString());Sql(db,"INSERT INTO meta VALUES('created',$0)",DateTime.UtcNow.ToString("O"));
                Sql(db,"INSERT INTO meta VALUES('environment',$0)",JsonSerializer.Serialize(new{dataPath,pluginsPath,cccPath}));
                if(initialPluginsHash!=HashFile(pluginsPath)||initialCccHash!=HashFile(cccPath))throw new IOException("Load-order inputs changed during indexing. Previous index preserved.");
                Sql(db,"INSERT INTO meta VALUES('pluginsHash',$0)",initialPluginsHash);Sql(db,"INSERT INTO meta VALUES('cccHash',$0)",initialCccHash);Sql(db,"PRAGMA optimize");
            }
            File.Move(temporary,database,true);Progress("Ready",order.Length,order.Length,total);return new{database,recordVersions=total,plugins=indexed,warnings,status=Status()};
        }catch{if(File.Exists(temporary))File.Delete(temporary);Progress("Index failed; previous index preserved",0,order.Length,total);throw;}
    }
    internal static string? Candidate(uint localId,int index,bool light){
        if(light&&(localId>0xFFF||index<0||index>0xFFF))return null;if(!light&&(localId>0xFFFFFF||index<0||index>0xFD))return null;
        return (light?0xFE000000u|((uint)index<<12)|localId:((uint)index<<24)|localId).ToString("X8");
    }
    internal static void Insert(SqliteConnection db,RecordData m,string winner,int ordinal,string? candidate){
        string metadata=JsonSerializer.Serialize(m);
        Sql(db,"INSERT OR REPLACE INTO records VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17)",m.Key,m.Name,m.EditorId,m.Type,m.Category,m.Origin,winner,m.LocalId,candidate,m.Deleted?1:0,m.Playable.HasValue?(m.Playable.Value?1:0):null,m.Enchanted?1:0,m.Weight,m.Value,m.Damage,m.Armor,JsonSerializer.Serialize(m.Keywords),metadata);
        Sql(db,"INSERT OR REPLACE INTO versions VALUES($0,$1,$2,$3,$4)",m.Key,winner,ordinal,m.Deleted?1:0,metadata);Sql(db,"DELETE FROM links WHERE source=$0",m.Key);
        foreach(string link in m.Links.Distinct())Sql(db,"INSERT OR IGNORE INTO links VALUES($0,$1)",m.Key,link);
    }
    void Progress(string message,int completed,int total,int records){var text=JsonSerializer.Serialize(new{message,completed,total,records,at=DateTime.UtcNow});string temp=Path.Combine(root,"progress.tmp");File.WriteAllText(temp,text);File.Move(temp,Path.Combine(root,"progress.json"),true);Console.Error.WriteLine(text);}
    static string HashFile(string path){if(!File.Exists(path))return "absent";using var f=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();}
    object Status(){
        if(!File.Exists(database))return new{ready=false,root,dataPath,pluginsPath};using var db=Open(database,true);var plugins=Rows(db,"SELECT * FROM plugins ORDER BY ordinal");
        var changed=plugins.Where(p=>{var f=new FileInfo(Path.Combine(dataPath,(string)p["name"]!));return !f.Exists||f.Length!=Convert.ToInt64(p["size"])||f.LastWriteTimeUtc.ToString("O")!=(string)p["modified"]!;}).Select(p=>p["name"]).ToArray();
        bool stale=changed.Length>0||Convert.ToString(Scalar(db,"SELECT value FROM meta WHERE key='schema'"))!=Schema.ToString()||Convert.ToString(Scalar(db,"SELECT value FROM meta WHERE key='pluginsHash'"))!=HashFile(pluginsPath)||Convert.ToString(Scalar(db,"SELECT value FROM meta WHERE key='cccHash'"))!=HashFile(cccPath);
        return new{ready=true,root,database,dataPath,pluginsPath,stale,changed,warnings=JsonSerializer.Deserialize<JsonElement>(Convert.ToString(Scalar(db,"SELECT value FROM meta WHERE key='warnings'"))??"[]"),created=Scalar(db,"SELECT value FROM meta WHERE key='created'"),records=Scalar(db,"SELECT count(*) FROM records"),versions=Scalar(db,"SELECT count(*) FROM versions"),plugins,categories=Rows(db,"SELECT category,count(*) AS count FROM records GROUP BY category ORDER BY category"),sqlite=Scalar(db,"select sqlite_version()"),runtimeLoaded="Unverified until the connected runtime resolves a form",availability="Static existence does not establish ownership, normal acquisition, or entitlement"};
    }
    object Search(JsonElement r){
        RequireIndex();using var db=Open(database,true);string q=Text(r,"query");if(q.Length>160)throw new InvalidDataException("Search is limited to 160 characters.");
        string pattern="%"+q.Replace("!","!!").Replace("%","!%").Replace("_","!_")+"%";
        var conditions=new List<string>{"(name LIKE $0 ESCAPE '!' OR editor LIKE $0 ESCAPE '!' OR key LIKE $0 ESCAPE '!')","($1='' OR category=$1)","($2='' OR origin=$2 OR winner=$2)","($3='' OR keywords LIKE '%'||$3||'%' OR EXISTS(SELECT 1 FROM links l JOIN records k ON k.key=l.target WHERE l.source=records.key AND k.category='Keywords' AND (k.editor LIKE '%'||$3||'%' OR k.name LIKE '%'||$3||'%')))","($4='' OR playable=CAST($4 AS INTEGER))","($5='' OR enchanted=CAST($5 AS INTEGER))"};
        if(Text(r,"deleted")!="include")conditions.Add("deleted=0");string order=Text(r,"sort") switch{"damage"=>"damage DESC","value"=>"value DESC","weight"=>"weight DESC","armor"=>"armor DESC",_=>"COALESCE(NULLIF(name,''),NULLIF(editor,''),key) COLLATE NOCASE,key"};
        conditions.Add("($6='' OR type=$6)");
        var keyList=r.TryGetProperty("keys",out var requestedKeys)&&requestedKeys.ValueKind==JsonValueKind.Array?requestedKeys.EnumerateArray().Take(512).Where(v=>v.ValueKind==JsonValueKind.String).Select(v=>v.GetString()).ToArray():Array.Empty<string?>();
        bool restricted=r.TryGetProperty("restrictKeys",out var restrict)&&restrict.ValueKind==JsonValueKind.True;
        conditions.Add("($7=0 OR key IN (SELECT value FROM json_each($8)))");
        var valuesList=new List<object?>{pattern,Text(r,"category"),Text(r,"plugin"),Text(r,"keyword"),Text(r,"playable"),Text(r,"enchanted"),Text(r,"type"),restricted?1:0,JsonSerializer.Serialize(keyList)};
        foreach(string field in new[]{"damage","weight","value","armor"})foreach(string side in new[]{"Min","Max"}){string name=field+side;if(!r.TryGetProperty(name,out var bound)||bound.ValueKind==JsonValueKind.Null)continue;if(!bound.TryGetDouble(out var n)||!double.IsFinite(n)||n<0||n>1e9)throw new InvalidDataException("Invalid numeric filter: "+name);conditions.Add(field+(side=="Min"?">=":"<=")+"$"+valuesList.Count);valuesList.Add(n);}
        var values=valuesList.ToArray();
        string where=string.Join(" AND ",conditions);int offset=Int(r,"offset",0,0,5000000),limit=Int(r,"limit",150,1,250);
        var rows=Rows(db,"SELECT key,name,editor,type,category,origin,winner,localId,candidate,playable,enchanted,weight,value,damage,armor FROM records WHERE "+where+" ORDER BY "+order+" LIMIT "+limit+" OFFSET "+offset,values);
        return new{rows,total=Rows(db,"SELECT count(*) AS count FROM records WHERE "+where,values)[0]["count"],offset,limit};
    }
    void RequireIndex(){if(!File.Exists(database))throw new FileNotFoundException("Build the local catalogue first.");}
    // Re-read one indexed winning record for research. Does not rewrite the cache,
    // modify the plugin, infer a runtime slot, or accept a caller-supplied path.
    object SourceRecord(string key){
        RequireIndex();if(key.Length>260)throw new InvalidDataException("Invalid FormKey.");using var db=Open(database,true);
        var row=Rows(db,"SELECT winner,type FROM records WHERE key=$0",key).FirstOrDefault()??throw new KeyNotFoundException("Record absent from this index.");
        string winner=(string)row["winner"]!,type=(string)row["type"]!;var modKey=ModKey.FromNameAndExtension(winner);
        if(Path.GetFileName(winner)!=winner)throw new InvalidDataException("Invalid indexed plugin name.");
        var active=LoadOrder.GetLoadOrderListings(GameRelease.SkyrimSE,pluginsPath,File.Exists(cccPath)?cccPath:null,dataPath).Any(x=>x.Enabled&&x.ModKey==modKey);
        if(!active)throw new InvalidDataException("Indexed winning plugin is no longer active. Rebuild the catalogue.");
        string path=Path.Combine(dataPath,winner);var info=new FileInfo(path);long size=info.Length;var modified=info.LastWriteTimeUtc;
        var recordType=RecordData.RecordTypes.FirstOrDefault(t=>t.Name=="I"+type+"Getter")??throw new InvalidDataException("Unsupported indexed record type.");
        using var mod=SkyrimMod.CreateFromBinaryOverlay(new ModPath(modKey,path),SkyrimRelease.SkyrimSE);
        var record=mod.EnumerateMajorRecords(recordType).FirstOrDefault(r=>r.FormKey.ToString()==key)??throw new KeyNotFoundException("Record changed or disappeared from the indexed winner. Rebuild the catalogue.");
        var data=RecordData.Read(record);info.Refresh();if(info.Length!=size||info.LastWriteTimeUtc!=modified)throw new IOException("Plugin changed during the selected record read.");
        return new{key,plugin=winner,record=data,readOnly=true,modified,size,note="Fresh disk metadata from the indexed winner; no runtime execution or cache modification. Rebuild the index after load-order changes."};
    }
    object CrossReference(string key){
        RequireIndex();if(key.Length>260)throw new InvalidDataException("Invalid FormKey.");using var db=Open(database,true);
        var rows=Rows(db,"SELECT metadata FROM records WHERE key=$0",key);if(rows.Count==0)return new{key,found=false,note="No indexed record for this stable identity."};
        var m=JsonSerializer.Deserialize<RecordData>((string)rows[0]["metadata"]!)!;
        var uses=Rows(db,"SELECT r.key,r.name,r.editor,r.category,r.type,r.winner FROM links l JOIN records r ON r.key=l.source WHERE l.target=$0 LIMIT 256",key);
        var count=Rows(db,"SELECT count(*) AS n FROM links WHERE target=$0",key)[0]["n"];
        return new{key,found=true,tomeAssociations=(m.Type=="Spell"||m.Type=="Book")?TomeAssociations(db,key,m.Type):null,acquisition=AcquisitionPaths(db,key),details=Detail(key),access=AccessEvidence(m,uses),incomingTotal=count,incomingTruncated=Convert.ToInt64(count)>256,incoming=uses,scripts=m.Fields.GetValueOrDefault("VirtualMachineAdapter"),runtimeInstances="Use Source reference for a selected live instance; static base records do not enumerate world instances."};
    }
    internal static object AccessEvidence(RecordData m,List<Dictionary<string,object?>> uses){
        var categories=uses.Select(r=>Convert.ToString(r.GetValueOrDefault("category"))).Distinct().ToArray();
        var leads=new List<string>();if(m.Playable==false)leads.Add("Hidden / non-playable flag");if(categories.Contains("Quests"))leads.Add("Quest reference");if(categories.Contains("Leveled Lists"))leads.Add("Leveled-list reference");if(categories.Contains("Containers"))leads.Add("Container reference");if(categories.Contains("NPCs"))leads.Add("NPC reference");if(categories.Contains("Books / Tomes"))leads.Add("Book/tome reference");if(categories.Contains("Recipes"))leads.Add("Crafting recipe reference");if(categories.Contains("Form Lists"))leads.Add("FormList membership");
        return new{classification=m.Playable==false?"Hidden / non-playable":"Unknown",leads,ownership=(bool?)null,entitlement=(bool?)null,note="Incoming references are acquisition/restriction leads, not proof of accessibility, vendor stock, eligibility or a grant path. No references does not prove unused/test."};
    }
    object Detail(string key){
        RequireIndex();if(key.Length>260)throw new InvalidDataException("Invalid FormKey.");using var db=Open(database,true);var rows=Rows(db,"SELECT * FROM records WHERE key=$0",key);if(rows.Count==0)throw new KeyNotFoundException("Record absent from this index.");
        var record=rows[0];record["metadata"]=JsonSerializer.Deserialize<JsonElement>((string)record["metadata"]!);var versions=Rows(db,"SELECT plugin,ordinal,deleted,metadata FROM versions WHERE key=$0 ORDER BY ordinal",key);foreach(var v in versions)v["metadata"]=JsonSerializer.Deserialize<JsonElement>((string)v["metadata"]!);
        var links=Rows(db,"SELECT r.key,r.name,r.category,r.winner FROM links l JOIN records r ON r.key=l.target WHERE l.source=$0 LIMIT 128",key);
        var usedBy=Rows(db,"SELECT r.key,r.name,r.category,r.winner FROM links l JOIN records r ON r.key=l.source WHERE l.target=$0 LIMIT 128",key);
        return new{record,versions,links,usedBy,relationshipLimit=128,runtime=new{verified=false,referenceInstance=(string?)null},authority=new{existence="Indexed active plugin record",normalAvailability="Unknown; relationships are research leads",ownership="Not inspected",serverEntitlement="Unknown"}};
    }
    object Assets(string key){
        RequireIndex();using var db=Open(database,true);var row=Rows(db,"SELECT metadata FROM records WHERE key=$0",key).FirstOrDefault()??throw new KeyNotFoundException("Select a record.");var data=JsonSerializer.Deserialize<RecordData>((string)row["metadata"]!)!;var results=new List<object>();var archivePaths=Directory.EnumerateFiles(dataPath,"*.bsa",SearchOption.TopDirectoryOnly).ToArray();
        foreach(string raw in data.Assets.Take(24)){
            string? relative=SafeAsset(raw);if(relative==null){results.Add(new{path=raw,error="Unsafe asset path rejected"});continue;}string loose=Path.Combine(dataPath,relative);var archives=new List<object>();
            if(!File.Exists(loose))foreach(string archivePath in archivePaths)try{var archive=Archive.CreateReader(GameRelease.SkyrimSE,archivePath);var file=archive.Files.FirstOrDefault(f=>f.Path.Replace('/','\\').Equals(relative,StringComparison.OrdinalIgnoreCase));if(file!=null)archives.Add(new{archive=Path.GetFileName(archivePath),size=file.Size});}catch(Exception e){archives.Add(new{archive=Path.GetFileName(archivePath),error=e.GetBaseException().Message});}
            results.Add(new{path=relative,looseExists=File.Exists(loose),loosePath=File.Exists(loose)?loose:null,archiveCandidates=archives,selection=File.Exists(loose)?"Loose file; runtime mount rules still apply":archives.Count>0?"Archive candidates; mounted priority unverified":"Not found",preview="Metadata only; no asset extracted"});
        }
        return new{rows=results,assetLimit=24,archivesScanned=archivePaths.Length};
    }
    internal static string? SafeAsset(string value){var p=value.Replace('/','\\');if(string.IsNullOrWhiteSpace(p)||Path.IsPathRooted(p)||p.Contains(':')||p.Split('\\').Any(x=>x==".."))return null;if(p.EndsWith(".nif",StringComparison.OrdinalIgnoreCase)&&!p.StartsWith("meshes\\",StringComparison.OrdinalIgnoreCase))p="meshes\\"+p;if(p.EndsWith(".dds",StringComparison.OrdinalIgnoreCase)&&!p.StartsWith("textures\\",StringComparison.OrdinalIgnoreCase))p="textures\\"+p;return p;}
    object Thumbnail(string key){
        RequireIndex();using var db=Open(database,true);var row=Rows(db,"SELECT metadata FROM records WHERE key=$0",key).FirstOrDefault()??throw new KeyNotFoundException("Select a record.");var data=JsonSerializer.Deserialize<RecordData>((string)row["metadata"]!)!;
        string relative=data.Assets.Select(SafeAsset).FirstOrDefault(p=>p!=null&&p.EndsWith(".dds",StringComparison.OrdinalIgnoreCase))??throw new InvalidDataException("This record has no direct DDS icon/texture. NIF model thumbnails require a separate renderer.");
        byte[] bytes;string source;string loose=Path.Combine(dataPath,relative);
        if(File.Exists(loose)){if(new FileInfo(loose).Length>8*1024*1024)throw new InvalidDataException("Preview limit is 8 MiB compressed.");bytes=File.ReadAllBytes(loose);source="Loose file: "+relative;}
        else{var candidates=new List<(string archive,Mutagen.Bethesda.Archives.IArchiveFile file)>();foreach(string archivePath in Directory.EnumerateFiles(dataPath,"*.bsa")){var archive=Archive.CreateReader(GameRelease.SkyrimSE,archivePath);var f=archive.Files.FirstOrDefault(x=>x.Path.Replace('/','\\').Equals(relative,StringComparison.OrdinalIgnoreCase));if(f!=null)candidates.Add((Path.GetFileName(archivePath),f));}if(candidates.Count!=1)throw new InvalidDataException(candidates.Count==0?"DDS asset not found.":"Multiple archives contain this asset. Resolve mounted priority before previewing.");if(candidates[0].file.Size>8*1024*1024)throw new InvalidDataException("Preview limit is 8 MiB compressed.");bytes=candidates[0].file.GetBytes();source=candidates[0].archive+": "+relative;}
        return new{source,format="PNG",image=Convert.ToBase64String(TexturePreview.Decode(bytes)),note="Direct record icon/texture preview with source transparency. A texture alpha channel can have other shader uses; this is not an equipped model or a runtime mount-priority claim."};
    }
}
