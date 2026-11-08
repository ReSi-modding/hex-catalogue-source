using Mutagen.Bethesda.Plugins;
using System.IO.Compression;
using System.Text;

namespace ScriptDesk.Catalogue;
internal static class TravelDoorChecks {
    internal static void Run(Action<bool,string> check){
        string directory=Path.Combine(Path.GetTempPath(),"ScriptDesk-Door-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);string path=Path.Combine(directory,"Fixture.esm");
        var door=FormKey.Factory("000101:Fixture.esm");var target=FormKey.Factory("000102:Fixture.esm");var cell=FormKey.Factory("000103:Fixture.esm");var world=FormKey.Factory("000104:Fixture.esm");
        CatalogueEngine.DoorRecord? Read(byte[] data,FormKey? wanted=null,string plugin="Fixture.esm",string[]? masters=null,CatalogueEngine.DoorReadBudget? budget=null){File.WriteAllBytes(path,data);return CatalogueEngine.ReadDoorReference(path,plugin,masters??Array.Empty<string>(),wanted??door,budget??new());}
        void Rejected(Action run,string message){bool rejected=false;try{run();}catch(InvalidDataException){rejected=true;}catch(EndOfStreamException){rejected=true;}check(rejected,message);}
        byte[] Body(uint peer=0x102)=>Concat(Sub("NAME",U32(0x105)),Sub("XTEL",Concat(U32(peer),Floats(10,20,30,0,0,(float)Math.PI),U32(0))));
        byte[] Interior(byte[] records)=>Group("CELL",0,Group(0x103,6,Group(0x103,9,records)));
        try{
            var body=Body();var record=Record(0x101,body);var value=Read(Interior(record))!;CatalogueEngine.ValidateDoorRecord(value,true);
            check(value.Key==door&&value.Target==target&&value.Base==FormKey.Factory("000105:Fixture.esm")&&value.Cell==cell&&value.World.IsNull,"Door lookup retains exact source, base, XTEL peer and interior parent");
            check(value.Position!.SequenceEqual(new double[]{10,20,30})&&Math.Abs(value.Rotation![2]-180)<0.001,"Door XTEL coordinates and radians become engine degrees");
            value=Read(Group("WRLD",0,Group(0x104,1,Group(0x103,6,Group(0x103,8,record)))))!;
            check(value.World==world&&value.Cell==cell,"Persistent exterior door resolves worldspace independently of runtime spatial cell");
            value=Read(Interior(Record(0x101,body,0x40000,true)))!;check(value.Target==target&&value.Position![0]==10,"Compressed selected door supports exact XTEL decode");
            var extended=Concat(Encoding.ASCII.GetBytes("XXXX"),BitConverter.GetBytes((ushort)4),U32(4),Encoding.ASCII.GetBytes("NAME"),BitConverter.GetBytes((ushort)0),U32(0x105),Sub("XTEL",Concat(U32(0x102),Floats(1,2,3,0,0,0))));
            check(Read(Interior(Record(0x101,extended)))!.Target==target,"Extended subrecord length and 28-byte XTEL remain supported");
            check(Read(Interior(record),FormKey.Factory("000999:Fixture.esm"))==null,"Unknown selected reference does not become another door");
            var noReadBudget=new CatalogueEngine.DoorReadBudget{Remaining=0};check(Read(Interior(record),door,"Other.esp",Array.Empty<string>(),noReadBudget)==null,"Plugin without the target master is skipped without scanning");
            value=Read(Interior(record),door,"Patch.esp",new[]{"Fixture.esm"})!;check(value.Key==door&&value.Target==target,"Override plugin maps master-owned door identities exactly");
            var lightBody=Concat(Sub("NAME",U32(0x01000105)),Sub("XTEL",Concat(U32(0x01000102),Floats(1,2,3,0,0,0))));value=Read(Group("CELL",0,Group(0x01000103,6,Group(0x01000103,9,Record(0x01000101,lightBody)))),FormKey.Factory("000101:Fixture.esl"),"Fixture.esl",new[]{"Skyrim.esm"})!;check(value.Key.ModKey.ToString()=="Fixture.esl"&&value.Target==FormKey.Factory("000102:Fixture.esl")&&value.Cell==FormKey.Factory("000103:Fixture.esl"),"Light-plugin file-local IDs remain exact FormKeys without guessed runtime FE slots");
            value=Read(Interior(Record(0x101,Body(0x106))),door,"Patch.esp",new[]{"Fixture.esm"})!;check(value.Target==FormKey.Factory("000106:Fixture.esm"),"Changed override XTEL supplies changed peer instead of base data");
            var deletion=Read(Interior(Record(0x101,Array.Empty<byte>(),0x20)))!;Rejected(()=>CatalogueEngine.ValidateDoorRecord(deletion,true),"Deleted winning override is rejected instead of reviving earlier XTEL");
            var disabled=Read(Interior(Record(0x101,body,0x800)))!;Rejected(()=>CatalogueEngine.ValidateDoorRecord(disabled,true),"Initially disabled door is not silently exposed as travel");
            var conditional=Read(Interior(Record(0x101,Concat(body,Sub("XESP",new byte[8])))))!;Rejected(()=>CatalogueEngine.ValidateDoorRecord(conditional,true),"Conditionally enabled source or target door requires a separately verified route");
            var noLink=Read(Interior(Record(0x101,Sub("NAME",U32(0x105)))))!;Rejected(()=>CatalogueEngine.ValidateDoorRecord(noLink,true),"A non-load door cannot produce a fabricated destination");
            var nanBody=Concat(Sub("NAME",U32(0x105)),Sub("XTEL",Concat(U32(0x102),Floats(float.NaN,0,0,0,0,0))));var nan=Read(Interior(Record(0x101,nanBody)))!;Rejected(()=>CatalogueEngine.ValidateDoorRecord(nan,true),"Nonfinite XTEL coordinates fail validation");
            Rejected(()=>Read(Interior(Concat(record,record))),"Duplicate exact reference within one plugin is rejected");
            Rejected(()=>Read(Interior(Record(0x101,Concat(body,Sub("NAME",U32(0x105)))))),"Duplicate base/XTEL subrecords cannot override silently");
            Rejected(()=>Read(Interior(Record(0x101,Concat(Sub("NAME",U32(0xff000105)),Sub("XTEL",Concat(U32(0x102),Floats(0,0,0,0,0,0))))))),"Invalid master slot cannot produce a guessed FormKey");
            Rejected(()=>Read(Interior(record),budget:new CatalogueEngine.DoorReadBudget{Remaining=0}),"Header work limit exposes unavailable instead of an incomplete destination");
            Rejected(()=>Read(Interior(record)[..^1]),"Truncated group or record is rejected");
            var wrong=Record(0x101,body);Encoding.ASCII.GetBytes("ACHR").CopyTo(wrong,0);Rejected(()=>Read(Interior(wrong)),"Exact selected ID with wrong record kind is rejected");
            var irrelevant=Group("BOOK",0,new byte[]{1,2,3});check(Read(Concat(irrelevant,Interior(record)))!.Target==target,"Unrelated top-level group bodies are skipped without interpretation");
            check(File.ReadAllBytes(path).SequenceEqual(Concat(irrelevant,Interior(record))),"Selected door reader does not modify plugin bytes");
        }finally{if(File.Exists(path))File.Delete(path);Directory.Delete(directory);}
    }
    static byte[] U32(uint n)=>BitConverter.GetBytes(n);
    static byte[] Floats(params float[] values)=>values.SelectMany(BitConverter.GetBytes).ToArray();
    static byte[] Concat(params byte[][] values)=>values.SelectMany(x=>x).ToArray();
    static byte[] Sub(string name,byte[] bytes)=>Concat(Encoding.ASCII.GetBytes(name),BitConverter.GetBytes((ushort)bytes.Length),bytes);
    static byte[] Record(uint id,byte[] body,uint flags=0,bool compressed=false){if(compressed){using var stream=new MemoryStream();using(var zip=new ZLibStream(stream,CompressionLevel.Optimal,true))zip.Write(body);body=Concat(U32((uint)body.Length),stream.ToArray());}return Concat(Encoding.ASCII.GetBytes("REFR"),U32((uint)body.Length),U32(flags),U32(id),new byte[8],body);}
    static byte[] Group(string label,int type,byte[] bytes)=>Group(BitConverter.ToUInt32(Encoding.ASCII.GetBytes(label)),type,bytes);
    static byte[] Group(uint label,int type,byte[] bytes)=>Concat(Encoding.ASCII.GetBytes("GRUP"),U32((uint)bytes.Length+24),U32(label),BitConverter.GetBytes(type),new byte[8],bytes);
}
