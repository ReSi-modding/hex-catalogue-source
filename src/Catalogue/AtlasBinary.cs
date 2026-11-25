using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace ScriptDesk.Catalogue;
internal sealed partial class CatalogueEngine {
    internal sealed class AtlasStrings {
        readonly string data;readonly Dictionary<string,Dictionary<uint,string>> tables=new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,(long size,DateTime modified,string hash)> sources=new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<string> Warnings=new();
        internal AtlasStrings(string data){this.data=data;}
        internal AtlasLabelSource[] Inputs()=>sources.OrderBy(x=>x.Key,StringComparer.OrdinalIgnoreCase).Select(x=>new AtlasLabelSource(x.Key,x.Value.size,x.Value.modified.ToString("O"),x.Value.hash)).ToArray();
        internal object[] Provenance()=>sources.OrderBy(x=>x.Key,StringComparer.OrdinalIgnoreCase).Select(x=>(object)new{file=Path.GetFileName(x.Key),size=x.Value.size,modified=x.Value.modified.ToString("O"),sha256=x.Value.hash}).ToArray();
        void Remember(string path){var f=new FileInfo(path);var size=f.Length;var modified=f.LastWriteTimeUtc;var hash=HashFile(path);f.Refresh();if(size!=f.Length||modified!=f.LastWriteTimeUtc||sources.TryGetValue(path,out var prior)&&(prior.size!=size||prior.modified!=modified||prior.hash!=hash))throw new IOException("Atlas string source changed while reading.");sources[path]=(size,modified,hash);}
        internal void Validate(){foreach(var p in sources){var f=new FileInfo(p.Key);if(!f.Exists||f.Length!=p.Value.size||f.LastWriteTimeUtc!=p.Value.modified||HashFile(p.Key)!=p.Value.hash)throw new IOException("Atlas string source changed while reading.");}}
        internal string Name(string plugin,byte[]? full,bool localized){
            if(full==null||full.Length==0)return "";if(!localized)return AtlasBinary.Z(full);
            if(full.Length!=4)return "";uint id=BinaryPrimitives.ReadUInt32LittleEndian(full);
            if(!tables.TryGetValue(plugin,out var table)){
                table=new();string relative="strings/"+Path.GetFileNameWithoutExtension(plugin)+"_english.strings";string loose=Path.Combine(data,relative.Replace('/',Path.DirectorySeparatorChar));byte[]? bytes=null;
                if(File.Exists(loose)){Remember(loose);bytes=File.ReadAllBytes(loose);}
                else{
                    string stem=Path.GetFileNameWithoutExtension(plugin);var hits=new List<(string path,byte[] bytes)>();
                    foreach(string archivePath in Directory.EnumerateFiles(data,"*.bsa").Where(p=>Path.GetFileNameWithoutExtension(p).Equals(stem,StringComparison.OrdinalIgnoreCase)||Path.GetFileNameWithoutExtension(p).StartsWith(stem+" - ",StringComparison.OrdinalIgnoreCase)).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase)){
                        try{var archive=Archive.CreateReader(GameRelease.SkyrimSE,archivePath);var file=archive.Files.FirstOrDefault(f=>f.Path.Replace('\\','/').Equals(relative,StringComparison.OrdinalIgnoreCase));if(file!=null){if(file.Size>32*1024*1024)throw new InvalidDataException("Atlas string table exceeds bounded size.");Remember(archivePath);hits.Add((archivePath,file.GetBytes()));}}
                        catch(Exception e)when(e is not IOException){Warnings.Add(Path.GetFileName(archivePath)+": English marker labels unavailable: "+e.GetBaseException().Message);}
                    }
                    if(hits.Count==1)bytes=hits[0].bytes;else if(hits.Count>1)Warnings.Add(plugin+": ambiguous English string-table source; editor labels retained.");
                }
                if(bytes!=null){
                    if(bytes.Length<8||bytes.Length>32*1024*1024)throw new InvalidDataException("Invalid atlas string-table size.");
                    uint count=BinaryPrimitives.ReadUInt32LittleEndian(bytes);uint size=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));long textStart=8L+count*8L;
                    if(count>2000000||textStart+size>bytes.Length)throw new InvalidDataException("Invalid atlas string-table bounds.");
                    for(int i=0;i<count;i++){uint key=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8+i*8));uint offset=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12+i*8));long start=textStart+offset;if(start>=bytes.Length)throw new InvalidDataException("Invalid atlas string-table offset.");int end=Array.IndexOf(bytes,(byte)0,(int)start);if(end<0)throw new InvalidDataException("Unterminated atlas string label.");table[key]=Encoding.UTF8.GetString(bytes,(int)start,end-(int)start);}
                }else Warnings.Add(plugin+": English string table unavailable; editor labels retained.");
                tables[plugin]=table;
            }
            return table.TryGetValue(id,out var label)?label:"";
        }
    }
    internal sealed class AtlasBinary {
        internal sealed record Version(string plugin,int ordinal,long fileOffset,uint flags,string? @base,string? cell,string? world,double[]? position,string semanticSha256);
        internal sealed class Geo {
            internal string Key="",Type="",Plugin="",Editor="",Name="",Base="",Cell="",World="",Parent="",Location="",Teleport="",Material="",SemanticSha="";
            internal int Ordinal;internal long Offset;internal uint Flags,ParentFlags,WorldFlags;internal bool Interior,Conditional,Marker,MineScript;
            internal int[]? Grid;internal double[]? Position;internal byte[]? Heights;internal ushort Icon;
            internal List<Version> Versions=new();
            internal bool Enabled=>(Flags&(0x20|0x800))==0&&!Conditional;
            internal Geo Copy()=>(Geo)MemberwiseClone();
        }
        internal sealed class Snapshot {
            internal readonly Dictionary<string,Geo> Worlds=new(StringComparer.OrdinalIgnoreCase),Cells=new(StringComparer.OrdinalIgnoreCase),Locations=new(StringComparer.OrdinalIgnoreCase),Bases=new(StringComparer.OrdinalIgnoreCase),Refs=new(StringComparer.OrdinalIgnoreCase),Lands=new(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string,Dictionary<string,int>> Coverage=new(StringComparer.OrdinalIgnoreCase);
            internal void ClearGeography(){Worlds.Clear();Cells.Clear();Locations.Clear();Refs.Clear();Lands.Clear();Coverage.Clear();}
        }
        readonly string path;readonly AtlasInput input;readonly AtlasStrings strings;readonly string[] names;readonly bool localized;
        int examined;
        internal AtlasBinary(string data,AtlasInput input,AtlasStrings strings){this.input=input;this.strings=strings;path=Path.Combine(data,input.name);var h=Header(path,input.name);names=h.masters.Append(input.name).ToArray();localized=h.localized;}
        internal static string Z(byte[] data){int end=Array.IndexOf(data,(byte)0);return Encoding.UTF8.GetString(data,0,end<0?data.Length:end);}
        internal static (bool light,bool localized,string[] masters) Header(string path,string plugin){
            using var f=File.OpenRead(path);using var br=new BinaryReader(f);var h=br.ReadBytes(24);if(h.Length!=24||Encoding.ASCII.GetString(h,0,4)!="TES4")throw new InvalidDataException("Invalid plugin header: "+plugin);int size=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4)));if(size>16*1024*1024)throw new InvalidDataException("Plugin header is too large.");var ss=Subs(Decode(h,br.ReadBytes(size)));uint flags=BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8));return((flags&0x200)!=0,(flags&0x80)!=0,ss.Where(p=>p.Key=="MAST").Select(p=>Z(p.Value)).ToArray());
        }
        string Key(uint raw){if(raw==0)return "";uint slot=raw>>24;if(slot>=names.Length)throw new InvalidDataException(input.name+": invalid master slot.");return(raw&0xffffff).ToString("X6")+":"+names[slot];}
        string Form(Dictionary<string,byte[]> ss,string tag)=>ss.TryGetValue(tag,out var b)&&b.Length>=4?Key(BinaryPrimitives.ReadUInt32LittleEndian(b)):"";
        internal static List<KeyValuePair<string,byte[]>> Subs(byte[] data){
            var result=new List<KeyValuePair<string,byte[]>>();int at=0;int? extended=null;
            while(at<data.Length){if(at+6>data.Length)throw new InvalidDataException("Truncated atlas subrecord.");string tag=Encoding.ASCII.GetString(data,at,4);int size=BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at+4));at+=6;
                if(tag=="XXXX"){if(size!=4||extended.HasValue||at+4>data.Length)throw new InvalidDataException("Invalid extended atlas subrecord.");extended=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)));at+=4;continue;}
                if(extended.HasValue){size=extended.Value;extended=null;}if(size<0||size>data.Length-at)throw new InvalidDataException("Invalid atlas subrecord bounds.");result.Add(new(tag,data.AsSpan(at,size).ToArray()));at+=size;
            }
            if(extended.HasValue)throw new InvalidDataException("Unfinished extended atlas subrecord.");return result;
        }
        static byte[] Decode(byte[] h,byte[] data){if((BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8))&0x40000)==0)return data;if(data.Length<4)throw new InvalidDataException("Truncated compressed atlas record.");int size=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data));if(size>32*1024*1024)throw new InvalidDataException("Atlas record exceeds bounded decompression.");using var ms=new MemoryStream(data,4,data.Length-4);using var z=new ZLibStream(ms,CompressionMode.Decompress);var b=new byte[size];z.ReadExactly(b);if(z.ReadByte()!=-1)throw new InvalidDataException("Atlas record decompression mismatch.");return b;}
        static void Put(Dictionary<string,Geo> records,Geo row){var value=row.Copy();value.Versions=records.TryGetValue(row.Key,out var prior)?new(prior.Versions):new();value.Versions.Add(new(row.Plugin,row.Ordinal,row.Offset,row.Flags,row.Base,row.Cell,row.World,row.Position,row.SemanticSha));records[row.Key]=value;}
        static string Material(Geo row){string text=(row.Name+" "+row.Editor).Replace(" ","").ToLowerInvariant();if(text.Contains("shellbug"))return "Shellbug Chitin";if(text.Contains("stonequarry")||text.Contains("minestone"))return "Quarried Stone";var names=new[]{"Shellbug Chitin","Blackreach Geode","Quarried Stone","Heart Stone","Quicksilver","Orichalcum","Corundum","Moonstone","Adamantine","Malachite","Amethyst","Stalhrim","Sapphire","Emerald","Diamond","Copper","Silver","Ebony","Topaz","Ruby","Gold","Iron","Clay","Tin"};foreach(string name in names)if(text.Contains(name.Replace(" ","").ToLowerInvariant()))return name;if(text.Contains("geode"))return "Blackreach Geode";return "Ore";}
        internal static bool HasMiningScript(byte[] data){
            using var ms=new MemoryStream(data);using var br=new BinaryReader(ms);if(data.Length<6)throw new InvalidDataException("Truncated VMAD header.");int version=br.ReadUInt16(),format=br.ReadUInt16(),count=br.ReadUInt16();if(version>6||format is not (1 or 2)||count>10000)throw new InvalidDataException("Unsupported atlas VMAD header.");
            string Str(){int n=br.ReadUInt16();if(n>ms.Length-ms.Position)throw new InvalidDataException("Truncated VMAD string.");return Encoding.UTF8.GetString(br.ReadBytes(n));}
            void Skip(int type){switch(type){case 1:ms.Position+=8;break;case 2:Str();break;case 3:case 4:ms.Position+=4;break;case 5:ms.Position++;break;default:if(type is >=11 and <=15){uint n=br.ReadUInt32();if(n>1000000)throw new InvalidDataException("VMAD array exceeds bounded size.");for(int i=0;i<n;i++)Skip(type-10);}else throw new InvalidDataException("Unsupported VMAD property type "+type);break;}if(ms.Position>ms.Length)throw new InvalidDataException("Truncated VMAD property.");}
            for(int i=0;i<count;i++){string name=Str();br.ReadByte();int properties=br.ReadUInt16();if(name.Equals("MineOreScript",StringComparison.OrdinalIgnoreCase))return true;for(int p=0;p<properties;p++){Str();int type=br.ReadByte();br.ReadByte();Skip(type);}}
            return false;
        }
        internal void ReadDefinitions(Snapshot installed,Snapshot? baseline,HashSet<string> oreKeys)=>Scan(installed,baseline,oreKeys,true);
        internal void ReadReferenceKeys(HashSet<string> oreKeys)=>Scan(new Snapshot(),null,oreKeys,false,true);
        internal void ReadGeography(Snapshot installed,Snapshot? baseline,HashSet<string> oreKeys)=>Scan(installed,baseline,oreKeys,false);
        void Scan(Snapshot installed,Snapshot? baseline,HashSet<string> oreKeys,bool definitions,bool identitiesOnly=false){
            using var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);using var mm=MemoryMappedFile.CreateFromFile(f,null,0,MemoryMappedFileAccess.Read,HandleInheritability.None,true);using var view=mm.CreateViewAccessor(0,0,MemoryMappedFileAccess.Read);var counts=new Dictionary<string,int>();
            void Walk(long start,long end,string world,string cell,int depth){
                if(depth>32)throw new InvalidDataException("Atlas plugin nesting exceeds bounded depth.");long at=start;
                while(at<end){if(++examined>12000000)throw new InvalidDataException("Atlas plugin record budget exceeded.");if(at+24>end)throw new InvalidDataException("Truncated atlas record/group: "+input.name);var h=new byte[24];view.ReadArray(at,h,0,24);string tag=Encoding.ASCII.GetString(h,0,4);uint size=BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
                    if(tag=="GRUP"){
                        if(size<24||at+size>end)throw new InvalidDataException("Invalid atlas group boundary: "+input.name);int type=BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(12));uint label=BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8));string top=Encoding.ASCII.GetString(h,8,4);
                        if(type!=0||(definitions?(top is "ACTI" or "FLOR" or "DOOR"):(top is "WRLD" or "CELL" or "LCTN"))){string w=type==1?Key(label):world;string c=type is 6 or 8 or 9 or 10?Key(label):cell;Walk(at+24,at+size,w,c,depth+1);}at+=size;continue;
                    }
                    if(at+24+size>end||size>32*1024*1024)throw new InvalidDataException("Invalid atlas record boundary: "+input.name);
                    bool wanted=definitions?(tag is "ACTI" or "FLOR" or "DOOR"):identitiesOnly?tag=="REFR":(tag is "WRLD" or "CELL" or "LCTN" or "REFR" or "LAND");
                    if(wanted){counts[tag]=counts.GetValueOrDefault(tag)+1;var bytes=new byte[checked((int)size)];view.ReadArray(at+24,bytes,0,bytes.Length);var decoded=Decode(h,bytes);var entries=Subs(decoded);var ss=new Dictionary<string,byte[]>();foreach(var p in entries){if(p.Key is "NAME" or "DATA" or "XTEL" or "XESP" or "XCLC" or "VHGT"){if(ss.ContainsKey(p.Key))throw new InvalidDataException("Ambiguous duplicate atlas subrecord: "+p.Key);}ss[p.Key]=p.Value;}
                        var r=new Geo{Key=Key(BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12))),Type=tag,Plugin=input.name,Ordinal=input.ordinal,Offset=at,Flags=BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8)),World=world,Cell=cell,Editor=ss.TryGetValue("EDID",out var ed)?Z(ed):""};
                        if(!identitiesOnly&&ss.TryGetValue("FULL",out var full))r.Name=strings.Name(input.name,full,localized);
                        void Save(Func<Snapshot,Dictionary<string,Geo>> family){r.SemanticSha=AtlasOrder.Semantic(decoded,names,cell,world,r.Flags);Put(family(installed),r);if(baseline!=null)Put(family(baseline),r);}
                        if(definitions){r.MineScript=ss.TryGetValue("VMAD",out var vm)&&HasMiningScript(vm);r.Material=Material(r);if(r.MineScript||tag=="DOOR")oreKeys.Add(r.Key);Save(s=>s.Bases);}
                        else if(tag=="WRLD"){r.Parent=Form(ss,"WNAM");r.Location=Form(ss,"XLCN");if(ss.TryGetValue("PNAM",out var pf)&&pf.Length>=2)r.ParentFlags=BinaryPrimitives.ReadUInt16LittleEndian(pf);if(ss.TryGetValue("DATA",out var wf)&&wf.Length>=1)r.WorldFlags=wf[0];Save(s=>s.Worlds);}
                        else if(tag=="CELL"){r.Interior=ss.TryGetValue("DATA",out var flags)&&flags.Length>0&&(flags[0]&1)!=0;if(r.Interior)r.World="";if(ss.TryGetValue("XCLC",out var grid)&&grid.Length>=8)r.Grid=new[]{BinaryPrimitives.ReadInt32LittleEndian(grid),BinaryPrimitives.ReadInt32LittleEndian(grid.AsSpan(4))};r.Parent=Form(ss,"XLCN");Save(s=>s.Cells);}
                        else if(tag=="LCTN"){r.Parent=Form(ss,"PNAM");Save(s=>s.Locations);}
                        else if(tag=="LAND"){r.Heights=ss.GetValueOrDefault("VHGT");if(r.Heights!=null&&r.Heights.Length!=1096)throw new InvalidDataException("Unsupported LAND/VHGT size.");Save(s=>s.Lands);}
                        else{
                            r.Base=Form(ss,"NAME");r.Teleport=Form(ss,"XTEL");r.Marker=ss.ContainsKey("XMRK");r.Conditional=ss.ContainsKey("XESP");
                            if(ss.TryGetValue("DATA",out var placement)&&placement.Length>=24){r.Position=Enumerable.Range(0,3).Select(i=>(double)BitConverter.ToSingle(placement,i*4)).ToArray();if(r.Position.Any(v=>!double.IsFinite(v)||Math.Abs(v)>10000000))throw new InvalidDataException("Invalid atlas placed coordinates.");}
                            if(ss.TryGetValue("TNAM",out var icon)&&icon.Length>=2)r.Icon=BinaryPrimitives.ReadUInt16LittleEndian(icon);
                            if(oreKeys.Contains(r.Base)||oreKeys.Contains(r.Key)||r.Teleport!=""||r.Marker||installed.Refs.ContainsKey(r.Key)){oreKeys.Add(r.Key);if(!identitiesOnly)Save(s=>s.Refs);}
                        }
                    }
                    at+=24+size;
                }
                if(at!=end)throw new InvalidDataException("Atlas group did not end on its declared boundary.");
            }
            Walk(0,f.Length,"","",0);if(!definitions&&!identitiesOnly){installed.Coverage[input.name]=counts;if(baseline!=null)baseline.Coverage[input.name]=counts;}
        }
    }
}
