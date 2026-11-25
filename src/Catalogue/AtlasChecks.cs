using System.Text;
using System.Text.Json;
using static ScriptDesk.Catalogue.CatalogueEngine;

namespace ScriptDesk.Catalogue;
internal static class AtlasChecks {
    internal static void Run(Action<bool,string> check){
        byte[] Vmad(string script){using var ms=new MemoryStream();using var w=new BinaryWriter(ms);w.Write((ushort)5);w.Write((ushort)2);w.Write((ushort)1);byte[] b=Encoding.UTF8.GetBytes(script);w.Write((ushort)b.Length);w.Write(b);w.Write((byte)0);w.Write((ushort)0);return ms.ToArray();}
        check(AtlasBinary.HasMiningScript(Vmad("MineOreScript")),"Atlas mining definition requires the exact attached script header");
        check(!AtlasBinary.HasMiningScript(Vmad("NotMineOreScriptSuffix")),"Atlas ignores a longer script name containing MineOreScript");
        var h=new byte[1096];BitConverter.GetBytes(10f).CopyTo(h,0);h[4]=unchecked((byte)-3);h[5]=2;h[4+33]=5;
        var heights=AtlasTerrain.Decode(h);check(heights[0]==56&&heights[1]==72&&heights[33]==96,"Atlas LAND decoder preserves signed deltas and independent row starts");
        bool bad=false;try{AtlasBinary.Subs(new byte[]{65,66,67,68,10,0,1});}catch(InvalidDataException){bad=true;}check(bad,"Atlas malformed subrecord cannot become partial valid coverage");
        string temp=Path.Combine(Path.GetTempPath(),"hex-atlas-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try{
            var strings=new AtlasStrings(temp);var env=new AtlasEnvironment(temp,Path.Combine(temp,"Plugins.txt"),Path.Combine(temp,"Skyrim.ccc"),"hash","absent");
            var input=new AtlasInput("Test.esm",0,0,1,"time","hash",Array.Empty<string>());
            AtlasBinary.Geo G(string key,string type,string cell="",string world="",double[]? position=null)=>new(){Key=key,Type=type,Plugin="Test.esm",Cell=cell,World=world,Position=position,Name=key};
            var s=new AtlasBinary.Snapshot();string world="000001:Test.esm",cell="000002:Test.esm",inner="000003:Test.esm",ore="000004:Test.esm",door="000005:Test.esm";
            s.Worlds[world]=G(world,"WRLD");s.Cells[cell]=G(cell,"CELL",world:world);s.Cells[cell].Grid=new[]{0,0};s.Cells[inner]=G(inner,"CELL");s.Cells[inner].Interior=true;
            s.Bases[ore]=G(ore,"ACTI");s.Bases[ore].MineScript=true;s.Bases[ore].Material="Iron";s.Bases[door]=G(door,"DOOR");
            var r=G("000010:Test.esm","REFR",cell,world,new double[]{10,20,30});r.Base=ore;s.Refs[r.Key]=r;
            JsonElement Pack()=>JsonSerializer.SerializeToElement(AtlasPack.Build(s,new[]{input},env,"test","Test",strings));
            var initial=Pack();check(initial.GetProperty("targets").GetArrayLength()==1,"Atlas includes an enabled exact mining placement");
            r.Flags=0x800;var disabled=Pack().GetProperty("targets")[0];check(disabled.GetProperty("browseOnly").GetBoolean()&&disabled.GetProperty("flags").GetProperty("initiallyDisabled").GetBoolean()&&disabled.GetProperty("doorSteps").GetArrayLength()==0,"Atlas retains initially disabled ore as browse-only without entrance guidance");
            r.Flags=0x20;check(Pack().GetProperty("targets").GetArrayLength()==0,"Atlas winning deleted ore reference removes the target");r.Flags=0;
            r.Cell=inner;r.World="";r.Position=new double[]{-50,-60,70};var moved=Pack().GetProperty("targets")[0];check(moved.GetProperty("spaceKey").GetString()=="cell:"+inner&&moved.GetProperty("position").GetProperty("x").GetDouble()==-50,"Atlas moved reference replaces parent space and coordinates together");
            var a=G("000011:Test.esm","REFR",cell,world,new double[]{1,2,3});a.Base=door;a.Teleport="000012:Test.esm";var b=G(a.Teleport,"REFR",inner,position:new double[]{4,5,6});b.Base=door;s.Refs[a.Key]=a;s.Refs[b.Key]=b;
            var linked=Pack();check(linked.GetProperty("doorGraph").GetProperty("edges").GetArrayLength()==1&&linked.GetProperty("targets")[0].GetProperty("doorSteps").GetArrayLength()==1,"Atlas only authored forward teleport connectivity creates an entrance chain");
            check(linked.GetProperty("landmarks").EnumerateArray().Any(x=>x.GetProperty("name").GetString()!.StartsWith("Arrival doorway from")),"Atlas one-way arrival endpoint does not invent a reverse Door to route");
            a.Conditional=true;check(Pack().GetProperty("doorGraph").GetProperty("edges").GetArrayLength()==0,"Atlas enable-parent doorway cannot form guidance");a.Conditional=false;a.Flags=0x800;check(Pack().GetProperty("doorGraph").GetProperty("edges").GetArrayLength()==0,"Atlas initially disabled doorway cannot form guidance");a.Flags=0;s.Bases[door].Type="ACTI";check(Pack().GetProperty("doorGraph").GetProperty("edges").GetArrayLength()==0,"Atlas non-door base with XTEL cannot become a verified doorway");s.Bases[door].Type="DOOR";
            var land=G("000020:Test.esm","LAND",cell,world);land.Heights=new byte[1096];s.Lands[land.Key]=land;var bounds=AtlasPack.Bounds(new[]{new double[]{0,0,0},new double[]{4096,4096,0}},0);
            string child="000021:Test.esm";s.Worlds[child]=G(child,"WRLD");s.Worlds[child].Parent=world;
            check(AtlasTerrain.Render(child,s,bounds)==null,"Atlas WNAM alone does not imply parent LAND inheritance");s.Worlds[child].ParentFlags=1;
            var inherited=AtlasTerrain.Render(child,s,bounds)!;check(Convert.ToInt32(inherited["cellCount"])==1&&JsonSerializer.Serialize(inherited["landRecords"]).Contains("\"inherited\":true"),"Atlas PNAM Use Land Data explicitly inherits cropped parent LAND with provenance");
            var duplicate=G("000022:Test.esm","LAND",cell,world);duplicate.Heights=new byte[1096];s.Lands[duplicate.Key]=duplicate;
            check(AtlasTerrain.Render(world,s,bounds)==null,"Atlas two distinct LAND identities at one coordinate never select an arbitrary relief winner");s.Lands.Remove(duplicate.Key);
            s.Worlds[child].WorldFlags=16;check(AtlasTerrain.Render(child,s,bounds)==null,"Atlas No Landscape world never receives inferred parent relief");s.Worlds[child].WorldFlags=0;s.Worlds[world].Parent=child;s.Worlds[world].ParentFlags=1;bad=false;try{AtlasTerrain.Render(child,s,bounds);}catch(InvalidDataException){bad=true;}check(bad,"Atlas parent-land inheritance cycle fails explicitly");
            var full=new AtlasInput("Full.esp",0,0,1,"","",Array.Empty<string>());var light=new AtlasInput("Light.esl",1,1,1,"","",Array.Empty<string>());
            var proofSnapshot=new AtlasBinary.Snapshot();var version=G("000030:Full.esp","REFR");version.Plugin=light.name;version.Versions=new(){new(full.name,0,0,0,"",cell,world,new double[]{1,2,3},"different-full"),new(light.name,1,0,0,"",cell,world,new double[]{4,5,6},"different-light")};proofSnapshot.Refs[version.Key]=version;
            check(!new AtlasOrder(new[]{full,light},default).Prove(proofSnapshot).verified,"Atlas incomparable different full/light records cannot establish a winner");
            light=light with{masters=new[]{full.name}};check(new AtlasOrder(new[]{full,light},default).Prove(proofSnapshot).verified,"Atlas declared master dependency proves the maximal full/light record winner");
            check(AtlasOrder.Semantic(new byte[]{1},new[]{"First.esm","Own.esp"},cell,world,0)!=AtlasOrder.Semantic(new byte[]{1},new[]{"Other.esm","Own.esp"},cell,world,0),"Atlas identical raw form bytes with different master mappings are not semantic equals");
            var large=Enumerable.Range(0,4350).Select(i=>new AtlasInput("Plugin"+i+(i<254?".esp":".esl"),i,i<254?0:1,1,"","",Array.Empty<string>())).ToArray();var watch=System.Diagnostics.Stopwatch.StartNew();var largeOrder=new AtlasOrder(large,default);watch.Stop();check(largeOrder.Acyclic&&largeOrder.Execution.Length==4350&&watch.ElapsedMilliseconds<10000,"Atlas maximum supported plugin partitions resolve without cubic closure work ("+watch.ElapsedMilliseconds+" ms)");
            var four=new[]{new AtlasInput("F0.esp",0,0,1,"","",Array.Empty<string>()),new AtlasInput("L0.esl",1,1,1,"","",Array.Empty<string>()),new AtlasInput("F1.esp",2,0,1,"","",Array.Empty<string>()),new AtlasInput("L1.esl",3,1,1,"","",Array.Empty<string>())};
            var permutations=new List<int[]>();void Permute(List<int> prefix){if(prefix.Count==4){var a=prefix.ToArray();if(Array.IndexOf(a,0)<Array.IndexOf(a,2)&&Array.IndexOf(a,1)<Array.IndexOf(a,3))permutations.Add(a);return;}foreach(int i in Enumerable.Range(0,4))if(!prefix.Contains(i)){prefix.Add(i);Permute(prefix);prefix.RemoveAt(prefix.Count-1);}}Permute(new());check(permutations.Count==6,"Atlas order oracle enumerates every full/light preserving interleaving");
            foreach(bool equal in new[]{false,true}){
                var v=G("000040:F0.esp","REFR");v.Plugin=four[3].name;v.Versions=new(){new(four[2].name,2,0,0,"",cell,world,null,"A"),new(four[3].name,3,0,0,"",cell,world,null,equal?"A":"B")};var fixture=new AtlasBinary.Snapshot();fixture.Refs[v.Key]=v;
                bool oracle=permutations.Select(ordering=>Array.IndexOf(ordering,2)>Array.IndexOf(ordering,3)?"A":equal?"A":"B").Distinct().Count()==1;
                check(new AtlasOrder(four,default).Prove(fixture).verified==oracle,"Atlas DAG winner proof agrees with all six actual interleavings for "+(equal?"equal":"different")+" semantics");
            }
            var missing=four.ToArray();missing[0]=missing[0] with{masters=new[]{"Missing.esm"}};check(!new AtlasOrder(missing,default).Acyclic,"Atlas missing master prevents an order proof");
            var cycle=four.ToArray();cycle[0]=cycle[0] with{masters=new[]{"L1.esl"}};cycle[3]=cycle[3] with{masters=new[]{"F1.esp"}};check(!new AtlasOrder(cycle,default).Acyclic,"Atlas cyclic full/light master constraints prevent an order proof");
            using var badRuntime=JsonDocument.Parse(JsonSerializer.Serialize(new{runtimePlugins=new{capturedAt=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),full=new[]{"Other.esp"},light=Array.Empty<string>()}}));bad=false;try{_ = new AtlasOrder(new[]{full},badRuntime.RootElement);}catch(InvalidDataException){bad=true;}check(bad,"Atlas new mismatched runtime partitions cannot inherit a cached order proof");
            byte[] Sub(string tag,byte[] body){using var ms=new MemoryStream();using var bw=new BinaryWriter(ms);bw.Write(Encoding.ASCII.GetBytes(tag));bw.Write((ushort)body.Length);bw.Write(body);return ms.ToArray();}
            byte[] Rec(string tag,uint key,uint flags,params byte[][] parts){var body=parts.SelectMany(x=>x).ToArray();var result=new byte[24+body.Length];Encoding.ASCII.GetBytes(tag).CopyTo(result,0);BitConverter.GetBytes(body.Length).CopyTo(result,4);BitConverter.GetBytes(flags).CopyTo(result,8);BitConverter.GetBytes(key).CopyTo(result,12);body.CopyTo(result,24);return result;}
            byte[] Group(string label,int type,byte[] body){var result=new byte[24+body.Length];Encoding.ASCII.GetBytes("GRUP").CopyTo(result,0);BitConverter.GetBytes(result.Length).CopyTo(result,4);(type==0?Encoding.ASCII.GetBytes(label):BitConverter.GetBytes(uint.Parse(label))).CopyTo(result,8);BitConverter.GetBytes(type).CopyTo(result,12);body.CopyTo(result,24);return result;}
            byte[] Ref(uint baseId)=>Rec("REFR",0x10,0,Sub("NAME",BitConverter.GetBytes(baseId)),Sub("DATA",new byte[24]));
            byte[] Cell(byte[] reference)=>Group("CELL",0,Rec("CELL",2,0,Sub("DATA",new byte[]{1})).Concat(Group("2",6,reference)).ToArray());
            File.WriteAllBytes(Path.Combine(temp,"Full.esp"),Rec("TES4",0,0).Concat(Group("ACTI",0,Rec("ACTI",4,0,Sub("VMAD",Vmad("MineOreScript"))).Concat(Rec("ACTI",6,0)).ToArray())).Concat(Cell(Ref(6))).ToArray());
            File.WriteAllBytes(Path.Combine(temp,"Light.esl"),Rec("TES4",0,0x200,Sub("MAST",Encoding.UTF8.GetBytes("Full.esp\0"))).Concat(Cell(Ref(4))).ToArray());
            var scan=new AtlasBinary.Snapshot();var keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var scanInputs=new[]{full,light};foreach(var q in scanInputs)new AtlasBinary(temp,q,strings).ReadDefinitions(scan,null,keys);
            for(int pass=0;pass<2;pass++){if(pass==1)scan.ClearGeography();foreach(var q in scanInputs)new AtlasBinary(temp,q,strings).ReadGeography(scan,null,keys);}
            check(scan.Refs["000010:Full.esp"].Versions.Count==2&&scan.Refs["000010:Full.esp"].Versions[0].@base=="000006:Full.esp","Atlas candidate-history pass retains an earlier uninteresting reference before a later ore override");
            using var request=JsonDocument.Parse(JsonSerializer.Serialize(new{dataPath=Path.Combine(temp,"missing-data"),pluginsPath=Path.Combine(temp,"missing-plugins.txt"),command="atlasExport"}));var engine=new CatalogueEngine(request.RootElement,temp);string cache=Path.GetDirectoryName(engine.DatabasePath)!;string receipt=Path.Combine(cache,"atlas-receipt.json");File.WriteAllText(receipt,"prior complete receipt");bad=false;try{engine.Execute(request.RootElement);}catch(IOException){bad=true;}check(bad&&File.ReadAllText(receipt)=="prior complete receipt","Atlas failed export preserves the last complete receipt");
            string progress=Path.Combine(cache,"progress.json");File.WriteAllText(progress,"unchanged locked progress");bad=false;
            using(var locked=new FileStream(progress,FileMode.Open,FileAccess.Read,FileShare.None))try{AtlasProgress("Atlas locked-progress regression",0,1,0);}catch(IOException){bad=true;}catch(UnauthorizedAccessException){bad=true;}
            check(!bad&&File.ReadAllText(progress)=="unchanged locked progress","Atlas read-only progress streams successfully while shared progress.json is locked");
        }finally{if(Directory.Exists(temp))Directory.Delete(temp,true);}
    }
}
