using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace ScriptDesk.Catalogue;
internal static class AcquisitionChecks {
    internal static void Run(){
        int count=0;void Check(bool ok,string name){if(!ok)throw new Exception("FAIL "+name);count++;Console.Error.WriteLine("PASS "+name);}
        Check(CatalogueEngine.TeachingSpell("{\"Fields\":{\"Teaches\":{\"Spell\":{\"FormKey\":\"021143:Skyrim.esm\"}}}}") == "021143:Skyrim.esm","Historical spell teaching link retains exact identity");
        Check(CatalogueEngine.TeachingSpell("{\"Fields\":{\"Teaches\":\"BookTeachesNothing\"}}") == null,"Patched tome does not falsely retain current learning");
        const string item="000001:Test.esm",list="000002:Test.esm",chest="000003:Test.esm";
        JsonElement Fields(object value)=>JsonSerializer.SerializeToElement(value);
        var link=new{FormKey=item};
        Check(CatalogueEngine.SupplyEdge("ConstructibleObject",Fields(new{Items=new[]{link},CreatedObject=new{FormKey="000099:Test.esm"}}),item)==null,"Recipe ingredient is not an acquisition output");
        var recipe=CatalogueEngine.SupplyEdge("ConstructibleObject",Fields(new{CreatedObject=link}),item);
        Check(recipe?.kind=="recipe-output"&&recipe?.path=="Fields.CreatedObject.FormKey","Crafted output keeps exact field provenance");
        Check(CatalogueEngine.SupplyEdge("Container",Fields(new{Items=new[]{new{Item=new{Count=2,Item=link}}}}),item)?.kind=="container-content","Nested container inventory is recognized");
        Check(CatalogueEngine.SupplyEdge("Npc",Fields(new{Items=new[]{new{Item=new{Item=link}}}}),item)?.kind=="npc-inventory","NPC inventory is distinct from arbitrary references");
        Check(CatalogueEngine.SupplyEdge("Npc",Fields(new{DefaultOutfit=link}),item)?.kind=="npc-outfit","NPC outfit can continue an equipment path");
        Check(CatalogueEngine.SupplyEdge("Outfit",Fields(new{Items=new[]{link}}),item)?.kind=="outfit-item","Outfit membership is retained");
        foreach(string type in new[]{"Flora","Tree"})Check(CatalogueEngine.SupplyEdge(type,Fields(new{Ingredient=link}),item)?.kind=="harvest-produce",type+" harvest ingredient is recognized");
        Check(CatalogueEngine.SupplyEdge("Book",Fields(new{Teaches=new{Spell=link}}),item)?.kind=="book-teaches","Vanilla book teaching remains a typed hypothesis");
        Check(CatalogueEngine.SupplyEdge("LeveledItem",Fields(new{Entries=new[]{new{Data=new{Level=4,Count=1,Reference=link}}}}),item)?.kind=="leveled-entry","Leveled entry supply is traced");
        Check(CatalogueEngine.SupplyEdge("LeveledItem",Fields(new{Entries=new[]{new{Conditions=new[]{link},Data=new{Reference=new{FormKey="000099:Test.esm"}}}}}),item)==null,"Entry conditions are never promoted to supplied items");
        Check(CatalogueEngine.SupplyEdge("Quest",Fields(new{Aliases=new[]{link}}),item)==null,"Quest linkage alone does not invent an acquisition route");
        Check(CatalogueEngine.SupplyEdge("Container",Fields(new{Items=new[]{new{Name=item}}}),item)==null,"Matching descriptive text is not a FormKey link");
        Check(CatalogueEngine.SupplyEdge("Container",Fields(new{Items=new[]{new{FormKey=item.ToUpperInvariant()}}}),item)!=null,"FormKey comparison ignores plugin filename case");
        using var db=new SqliteConnection("Data Source=:memory:");db.Open();CatalogueEngine.CreateSchema(db);
        void Add(string key,string type,object fields,params string[] links){CatalogueEngine.Insert(db,new RecordData{Key=key,Origin="Test.esm",Name=key,Type=type,Fields=JsonSerializer.Deserialize<Dictionary<string,object?>>(JsonSerializer.Serialize(fields))!,Links=links.ToList()},"Test.esm",0,"");}
        Add(item,"Weapon",new{});Add(list,"LeveledItem",new{Entries=new[]{new{Data=new{Reference=link}}}},item);
        Add(chest,"Container",new{Items=new[]{new{Item=new{Item=new{FormKey=list}}}}},list);
        Add("000004:Test.esm","ConstructibleObject",new{CreatedObject=link,Conditions=new[]{new{Data=new{Function="HasPerk"}}},WorkbenchKeyword=new{FormKey="000088:Test.esm"}},item);
        JsonElement Read()=>JsonSerializer.SerializeToElement(CatalogueEngine.AcquisitionPaths(db,item));
        var result=Read();var paths=result.GetProperty("paths");
        Check(paths.EnumerateArray().Any(p=>p.GetProperty("source").GetString()==chest&&p.GetProperty("steps").GetArrayLength()==2),"Item to leveled list to container is a concrete two-edge hypothesis");
        var crafting=paths.EnumerateArray().Single(p=>p.GetProperty("kind").GetString()=="recipe-output");
        Check(crafting.GetProperty("steps")[0].GetProperty("evidence").GetProperty("Conditions")[0].GetProperty("Data").GetProperty("Function").GetString()=="HasPerk","Recipe conditions accompany the path without evaluation");
        Check(paths.EnumerateArray().All(p=>p.GetProperty("liveAvailability").ValueKind==JsonValueKind.Null&&!p.GetProperty("conditionsEvaluated").GetBoolean()),"Static paths do not imply live availability");
        Add("000005:Test.esm","LeveledItem",new{Entries=new[]{new{Data=new{Reference=new{FormKey=list}}}}},list);
        Add(list,"LeveledItem",new{Entries=new[]{new{Data=new{Reference=link}},new{Data=new{Reference=new{FormKey="000005:Test.esm"}}}}},item,"000005:Test.esm");
        Check(Read().GetProperty("paths").GetArrayLength()<10,"Cyclic lists terminate through path ancestry");
        Add("000006:Test.esm","Container",new{Items=new[]{new{FormKey="000099:Test.esm"}}},item);
        Check(Read().GetProperty("unresolved").EnumerateArray().Any(p=>p.GetProperty("source").GetString()=="000006:Test.esm"),"An indexed link with missing projected contents remains unresolved");
        CatalogueEngine.Insert(db,new RecordData{Key="000088:Test.esm",Origin="Test.esm",Type="Keyword",EditorId="CraftingSmithingSharpeningWheel"},"Test.esm",0,"");
        Check(Read().GetProperty("paths").EnumerateArray().Any(p=>p.GetProperty("source").GetString()=="000004:Test.esm"&&p.GetProperty("kind").GetString()=="recipe-upgrade"),"Verified sharpening station identifies an upgrade rather than a new item");
        Add("000007:Test.esm","ConstructibleObject",new{CreatedObject=link,Conditions=new string('x',6000)},item);
        var large=Read().GetProperty("paths").EnumerateArray().Single(p=>p.GetProperty("source").GetString()=="000007:Test.esm");
        Check(large.GetProperty("steps")[0].GetProperty("evidence").GetProperty("Conditions").GetProperty("truncated").GetBoolean(),"Large repeated evidence is bounded with an explicit detail pointer");
        CatalogueEngine.Insert(db,new RecordData{Key="000008:Test.esm",Origin="Test.esm",Type="Container",Deleted=true,Fields=new(){{"Items",new[]{link}}},Links=new(){item}},"Test.esm",0,"");
        Check(!Read().GetProperty("paths").EnumerateArray().Any(p=>p.GetProperty("source").GetString()=="000008:Test.esm"),"Deleted winning records cannot create acquisition paths");
        for(int i=100;i<170;i++)Add(i.ToString("X6")+":Test.esm","Container",new{Items=new[]{link}},item);
        var beforeRows=JsonSerializer.Serialize(CatalogueEngine.Rows(db,"SELECT key,metadata FROM records ORDER BY key"));
        result=Read();Check(result.GetProperty("truncated").GetBoolean()&&result.GetProperty("inspected").GetInt32()<=256&&result.GetProperty("paths").GetArrayLength()<=96,"High-fanout acquisition search is explicitly bounded");
        Check(JsonSerializer.Serialize(CatalogueEngine.Rows(db,"SELECT key,metadata FROM records ORDER BY key"))==beforeRows,"Read-only acquisition traversal leaves indexed rows intact");
        Console.WriteLine(count+" acquisition checks passed.");
    }
}
