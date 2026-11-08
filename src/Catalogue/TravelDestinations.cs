using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using System.Text.Json;
using System.IO.MemoryMappedFiles;
using System.IO.Compression;
using System.Buffers.Binary;
using System.Diagnostics;

namespace ScriptDesk.Catalogue;
internal sealed partial class CatalogueEngine {
    internal sealed record DoorRecord(FormKey Key,FormKey Base,FormKey Cell,FormKey World,uint Flags,FormKey Target,double[]? Position,double[]? Rotation,bool Conditional=false);
    internal sealed class DoorReadBudget {
        internal int Remaining=8000000;readonly Stopwatch watch=Stopwatch.StartNew();
        internal void Step(){if(--Remaining<0||watch.ElapsedMilliseconds>8000)throw new InvalidDataException("Door lookup exceeded its bounded read. Destination is unavailable.");}
    }
    // Locate only a supplied REFR and its XTEL peer. Other record bodies are never
    // read or indexed; unrelated top-level groups are skipped in one step.
    internal static DoorRecord? ReadDoorReference(string path,string plugin,string[] masters,FormKey wanted,DoorReadBudget budget) {
        if(wanted.ID>0xffffff)throw new InvalidDataException("Invalid local door Form ID.");var names=masters.Append(plugin).Select(x=>ModKey.FromNameAndExtension(x)).ToArray();int slot=Array.IndexOf(names,wanted.ModKey);
        if(slot<0)return null;if(slot>255)throw new InvalidDataException("Invalid plugin master mapping.");uint rawWanted=((uint)slot<<24)|wanted.ID;
        FormKey Resolve(uint raw){if(raw==0)return FormKey.Null;uint ix=raw>>24;if(ix>=names.Length)throw new InvalidDataException("Door record references an invalid master index.");return new FormKey(names[ix],raw&0xffffff);}
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);if(file.Length<24)throw new InvalidDataException("Truncated door source plugin.");
        using var mapped=MemoryMappedFile.CreateFromFile(file,null,0,MemoryMappedFileAccess.Read,HandleInheritability.None,true);
        using var view=mapped.CreateViewAccessor(0,0,MemoryMappedFileAccess.Read);
        var groups=new Stack<(long end,int type,uint label)>();long offset=0;DoorRecord? result=null;
        while(offset<file.Length){
            budget.Step();while(groups.Count>0&&offset>=groups.Peek().end)groups.Pop();long limit=groups.Count>0?groups.Peek().end:file.Length;
            if(offset+24>limit)throw new InvalidDataException("Truncated record/group in door lookup.");
            uint signature=view.ReadUInt32(offset),size=view.ReadUInt32(offset+4);
            if(signature==0x50555247){ // GRUP
                if(size<24||offset+size>limit)throw new InvalidDataException("Invalid group boundary in door lookup.");
                uint label=view.ReadUInt32(offset+8);int type=view.ReadInt32(offset+12);
                if(type==0&&label!=0x4c4c4543&&label!=0x444c5257){offset+=size;continue;} // CELL / WRLD only
                if(groups.Count>=32)throw new InvalidDataException("Door group nesting exceeds bounded depth.");groups.Push((offset+size,type,label));offset+=24;continue;
            }
            if(offset+24+size>limit)throw new InvalidDataException("Invalid record boundary in door lookup.");
            if(view.ReadUInt32(offset+12)==rawWanted){
                if(signature!=0x52464552)throw new InvalidDataException("Selected identity is not a placed door reference.");
                if(result!=null)throw new InvalidDataException("Duplicate selected door identity in a plugin.");
                if(size>1024*1024)throw new InvalidDataException("Door record exceeds bounded size.");
                uint flags=view.ReadUInt32(offset+8);var bytes=new byte[(int)size];view.ReadArray(offset+24,bytes,0,bytes.Length);
                if((flags&0x40000)!=0){
                    if(bytes.Length<4)throw new InvalidDataException("Truncated compressed door record.");
                    int expected=BinaryPrimitives.ReadInt32LittleEndian(bytes);if(expected<0||expected>2*1024*1024)throw new InvalidDataException("Door decompression exceeds bounded size.");
                    using var compressed=new MemoryStream(bytes,4,bytes.Length-4);using var zip=new ZLibStream(compressed,CompressionMode.Decompress);var decoded=new byte[expected];zip.ReadExactly(decoded);if(zip.ReadByte()!=-1)throw new InvalidDataException("Door decompression size mismatch.");bytes=decoded;
                }
                var cellGroup=groups.FirstOrDefault(x=>x.type is 6 or 8 or 9 or 10);var worldGroup=groups.FirstOrDefault(x=>x.type==1);
                FormKey baseKey=FormKey.Null,target=FormKey.Null;double[]? position=null,rotation=null;int at=0;int? extended=null;bool conditional=false;var seen=new HashSet<uint>();
                while(at<bytes.Length){
                    if(at+6>bytes.Length)throw new InvalidDataException("Truncated door subrecord.");uint tag=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));int length=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at+4));at+=6;
                    if(tag==0x58585858){if(length!=4||at+4>bytes.Length||extended!=null)throw new InvalidDataException("Invalid extended door subrecord.");extended=BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));at+=4;continue;}
                    if(extended.HasValue){length=extended.Value;extended=null;}if(length<0||length>bytes.Length-at)throw new InvalidDataException("Invalid door subrecord length.");
                    if(tag==0x50534558)conditional=true; // XESP enable-parent condition
                    if(tag is 0x454d414e or 0x4c455458){if(!seen.Add(tag))throw new InvalidDataException("Duplicate door identity/teleport subrecord.");}
                    if(tag==0x454d414e){if(length!=4)throw new InvalidDataException("Invalid door base identity.");baseKey=Resolve(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)));}
                    if(tag==0x4c455458){
                        if(length!=28&&length!=32)throw new InvalidDataException("Unsupported door teleport record.");target=Resolve(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)));
                        position=Enumerable.Range(0,3).Select(i=>(double)BitConverter.ToSingle(bytes,at+4+i*4)).ToArray();rotation=Enumerable.Range(0,3).Select(i=>(double)BitConverter.ToSingle(bytes,at+16+i*4)*180/Math.PI).ToArray();
                    }
                    at+=length;
                }
                if(extended.HasValue)throw new InvalidDataException("Unfinished extended door subrecord.");
                result=new(wanted,baseKey,Resolve(cellGroup.label),Resolve(worldGroup.label),flags,target,position,rotation,conditional);
            }
            offset+=24+size;
        }
        return result;
    }
    internal static void ValidateDoorRecord(DoorRecord value,bool requireTeleport){
        if((value.Flags&(0x20|0x800))!=0)throw new InvalidDataException("Selected door is deleted or initially disabled.");
        if(value.Conditional)throw new InvalidDataException("Conditionally enabled door requires a separately verified route.");
        if(value.Base.IsNull||value.Cell.IsNull)throw new InvalidDataException("Door base or parent space is unknown.");
        if(requireTeleport&&(value.Target.IsNull||value.Position==null||value.Rotation==null))throw new InvalidDataException("Selected door has no recorded load-door destination.");
        if(value.Position!=null&&value.Position.Any(x=>!double.IsFinite(x)||Math.Abs(x)>1e7)||value.Rotation!=null&&value.Rotation.Any(x=>!double.IsFinite(x)||Math.Abs(x)>36000))throw new InvalidDataException("Door arrival coordinates are invalid.");
    }
    object TravelDoor(JsonElement request){
        RequireIndex();string text=Text(request,"key");if(text.Length==0||text.Length>260)throw new InvalidDataException("An exact source door FormKey is required.");var key=FormKey.Factory(text);if(key.IsNull)throw new InvalidDataException("An exact source door FormKey is required.");
        using var db=Open(database,true);string listingHash=HashFile(pluginsPath),cccHash=HashFile(cccPath);
        if(Convert.ToString(Scalar(db,"SELECT value FROM meta WHERE key='pluginsHash'"))!=listingHash||Convert.ToString(Scalar(db,"SELECT value FROM meta WHERE key='cccHash'"))!=cccHash)throw new InvalidDataException("Load order changed. Refresh the catalogue first.");
        var active=LoadOrder.GetLoadOrderListings(GameRelease.SkyrimSE,pluginsPath,File.Exists(cccPath)?cccPath:null,dataPath).Where(x=>x.Enabled).Select(x=>x.ModKey).ToHashSet();
        var rows=Rows(db,"SELECT name,size,modified,masters FROM plugins ORDER BY ordinal");if(rows.Count==0||rows.Count>512||!active.SetEquals(rows.Select(x=>ModKey.FromNameAndExtension((string)x["name"]!))))throw new InvalidDataException("Active plugin identity differs from the bounded catalogue.");
        var files=new List<(string path,string name,string[] masters,long size,DateTime modified)>();
        foreach(var row in rows){string name=(string)row["name"]!;if(Path.GetFileName(name)!=name)throw new InvalidDataException("Invalid plugin identity.");var info=new FileInfo(Path.Combine(dataPath,name));if(!info.Exists||info.Length!=Convert.ToInt64(row["size"])||info.LastWriteTimeUtc.ToString("O")!=Convert.ToString(row["modified"]))throw new InvalidDataException("Door source changed. Refresh the catalogue first.");files.Add((info.FullName,name,JsonSerializer.Deserialize<string[]>((string)row["masters"]!)??throw new InvalidDataException("Plugin masters unavailable."),info.Length,info.LastWriteTimeUtc));}
        var budget=new DoorReadBudget();var versions=new List<object>();
        DoorRecord Find(FormKey wanted){DoorRecord? found=null;foreach(var f in files){var candidate=ReadDoorReference(f.path,f.name,f.masters,wanted,budget);if(candidate!=null){found=candidate;versions.Add(new{key=wanted.ToString(),plugin=f.name,deleted=(candidate.Flags&0x20)!=0});}}return found??throw new InvalidDataException("Exact door reference not found within the supplied plugin set.");}
        var source=Find(key);ValidateDoorRecord(source,true);var target=Find(source.Target);ValidateDoorRecord(target,false);
        foreach(var door in new[]{source,target}){if(Rows(db,"SELECT key FROM records WHERE key=$0 AND type='Door' AND deleted=0",door.Base.ToString()).Count!=1)throw new InvalidDataException("Exact reference does not resolve to an indexed door base.");if(Rows(db,"SELECT key FROM records WHERE key=$0 AND type='Cell' AND deleted=0",door.Cell.ToString()).Count!=1)throw new InvalidDataException("Door's owning cell is missing or deleted.");}
        var sourceSpace=source.World.IsNull?source.Cell:source.World;var targetSpace=target.World.IsNull?target.Cell:target.World;string sourceType=source.World.IsNull?"Cell":"WorldSpace",targetType=target.World.IsNull?"Cell":"WorldSpace";
        var sourceInfo=Rows(db,"SELECT key FROM records WHERE key=$0 AND type=$1 COLLATE NOCASE AND deleted=0",sourceSpace.ToString(),sourceType);var targetInfo=Rows(db,"SELECT name,editor FROM records WHERE key=$0 AND type=$1 COLLATE NOCASE AND deleted=0",targetSpace.ToString(),targetType);if(sourceInfo.Count!=1||targetInfo.Count!=1)throw new InvalidDataException("Door parent space is missing or deleted in the catalogue.");
        foreach(var f in files){var info=new FileInfo(f.path);if(!info.Exists||info.Length!=f.size||info.LastWriteTimeUtc!=f.modified)throw new IOException("Door source changed during lookup.");}if(HashFile(pluginsPath)!=listingHash||HashFile(cccPath)!=cccHash)throw new IOException("Load order changed during door lookup.");
        string label=Convert.ToString(targetInfo[0]["name"])??"";if(String.IsNullOrWhiteSpace(label))label=Convert.ToString(targetInfo[0]["editor"])??"";if(String.IsNullOrWhiteSpace(label))label=targetSpace.ToString();
        return new{destination=new{version=1,backend="door",sourceKey=source.Key.ToString(),sourceBaseKey=source.Base.ToString(),sourceSpaceKey=sourceSpace.ToString(),sourceSpaceType=sourceType,targetDoorKey=source.Target.ToString(),spaceKey=targetSpace.ToString(),spaceType=targetType,position=source.Position,rotation=source.Rotation,label},versions,readOnly=true,note="Exact recorded load-door link. Normal source-door reach, live restrictions and received relocation must be verified. This does not authorize arbitrary travel."};
    }
    internal static object? TravelMarker(IPlacedObjectGetter placed,string cellKey,string label) {
        // COCMarkerHeading is verified from the installed catalogue. Ordinary quest
        // markers and cell origins are not arrival points.
        if(placed.IsDeleted||placed.Base.FormKey!=FormKey.Factory("000032:Skyrim.esm")||
           (placed.MajorRecordFlagsRaw&0x800)!=0||placed.EnableParent!=null||placed.Placement==null)return null;
        var p=placed.Placement.Position;var r=placed.Placement.Rotation;
        double[] position={p.X,p.Y,p.Z},rotation={r.X*180/Math.PI,r.Y*180/Math.PI,r.Z*180/Math.PI};
        if(position.Any(n=>!double.IsFinite(n)||Math.Abs(n)>1e7)||rotation.Any(n=>!double.IsFinite(n)||Math.Abs(n)>36000))return null;
        return new{version=1,label,spaceKey=cellKey,spaceType="Cell",position,rotation,markerKey=placed.FormKey.ToString(),route="Cell arrival marker"};
    }
    object TravelDestinations(JsonElement request) {
        RequireIndex();string cellText=Text(request,"key");if(cellText.Length>260)throw new InvalidDataException("Invalid cell identity.");
        var cellKey=FormKey.Factory(cellText);using var db=Open(database,true);
        var record=Rows(db,"SELECT name,editor FROM records WHERE key=$0 AND type='Cell' AND deleted=0",cellKey.ToString()).SingleOrDefault()??throw new InvalidDataException("Select a cell from the location search.");
        var rows=Rows(db,"SELECT plugin FROM versions WHERE key=$0 ORDER BY ordinal",cellKey.ToString());
        if(rows.Count==0||rows.Count>256)throw new InvalidDataException("Cell source history unavailable.");
        string listingHash=HashFile(pluginsPath),cccHash=HashFile(cccPath);
        if(Convert.ToString(Scalar(db,"SELECT value FROM meta WHERE key='pluginsHash'"))!=listingHash||Convert.ToString(Scalar(db,"SELECT value FROM meta WHERE key='cccHash'"))!=cccHash)throw new InvalidDataException("Load order changed. Refresh the catalogue first.");
        var activeMods=LoadOrder.GetLoadOrderListings(GameRelease.SkyrimSE,pluginsPath,File.Exists(cccPath)?cccPath:null,dataPath).Where(x=>x.Enabled).Select(x=>x.ModKey).ToHashSet();
        var destinations=new Dictionary<FormKey,object?>();bool interior=false,deleted=false;
        var checkedFiles=new List<(string path,long size,DateTime modified)>();
        foreach(var row in rows){
            string plugin=(string)row["plugin"]!;var modKey=ModKey.FromNameAndExtension(plugin);
            if(Path.GetFileName(plugin)!=plugin||!activeMods.Contains(modKey))throw new InvalidDataException("Cell plugin is no longer active.");
            var stamp=Rows(db,"SELECT size,modified FROM plugins WHERE name=$0",plugin).Single();var info=new FileInfo(Path.Combine(dataPath,plugin));
            if(!info.Exists||info.Length!=Convert.ToInt64(stamp["size"])||info.LastWriteTimeUtc.ToString("O")!=Convert.ToString(stamp["modified"]))throw new InvalidDataException("Cell source changed. Refresh the catalogue first.");
            checkedFiles.Add((info.FullName,info.Length,info.LastWriteTimeUtc));
            using var mod=SkyrimMod.CreateFromBinaryOverlay(new ModPath(modKey,info.FullName),SkyrimRelease.SkyrimSE);
            var cell=mod.EnumerateMajorRecords<ICellGetter>().FirstOrDefault(x=>x.FormKey==cellKey)??throw new InvalidDataException("Cell source disappeared.");
            interior=((int)cell.Flags&1)!=0;deleted=cell.IsDeleted;
            var placements=cell.Persistent.Concat(cell.Temporary).Take(20001).ToArray();if(placements.Length>20000)throw new InvalidDataException("Cell exceeds the bounded arrival-marker scan.");
            if(placements.Select(x=>x.FormKey).Distinct().Count()!=placements.Length)throw new InvalidDataException("Ambiguous placed identities in cell.");
            foreach(var item in placements){if(item is IPlacedObjectGetter placed)destinations[item.FormKey]=TravelMarker(placed,cellText,Convert.ToString(record["name"])??Convert.ToString(record["editor"])??cellText);else destinations[item.FormKey]=null;}
        }
        foreach(var stamp in checkedFiles){var info=new FileInfo(stamp.path);if(!info.Exists||info.Length!=stamp.size||info.LastWriteTimeUtc!=stamp.modified)throw new IOException("Cell source changed during arrival-marker read.");}
        if(HashFile(pluginsPath)!=listingHash||HashFile(cccPath)!=cccHash)throw new IOException("Load order changed during arrival-marker read.");
        var results=interior&&!deleted?destinations.Values.Where(x=>x!=null).ToArray():Array.Empty<object?>();
        return new{rows=results,key=cellText,note=results.Length==0?"No usable interior arrival marker. Visit this location and mark a spot instead.":"Interior arrival markers from this cell's indexed overrides. Runtime identity and character readiness are checked before moving."};
    }
}
