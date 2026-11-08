using System.Text.Json;

namespace ScriptDesk.Catalogue;
internal sealed class ModelVariant {
    public string id {get;init;}="";
    public string label {get;init;}="";
    public string record {get;init;}="";
    public string path {get;init;}="";
    public string pose {get;init;}="Static";
    public string sex {get;init;}="";
    public int? weight {get;init;}
    public bool derivedWeightPath {get;init;}
    public string slots {get;init;}="";
    public string[] races {get;init;}=Array.Empty<string>();
    public string priority {get;init;}="";
    public object? textureSwaps {get;init;}
    public JsonElement alternateTextures {get;init;}=JsonSerializer.SerializeToElement(Array.Empty<object>());
    public string source {get;set;}="";
    public bool available {get;set;}
    public string error {get;set;}="";
}
internal static class ModelVariants {
    static JsonElement Fields(RecordData data)=>JsonSerializer.SerializeToElement(data.Fields);
    static JsonElement At(JsonElement e,params string[] names){foreach(var n in names){if(e.ValueKind!=JsonValueKind.Object||!e.TryGetProperty(n,out e))return default;}return e;}
    static string Text(JsonElement e)=>e.ValueKind==JsonValueKind.String?e.GetString()!:e.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False?e.ToString():"";
    static string File(JsonElement e){var direct=Text(At(e,"File","DataRelativePath"));return direct!=""?direct:Text(At(e,"Model","File","DataRelativePath"));}
    static IEnumerable<string> Links(JsonElement e){if(e.ValueKind==JsonValueKind.Array){foreach(var item in e.EnumerateArray())foreach(var key in Links(item))yield return key;}else{string key=Text(At(e,"FormKey"));if(key!=""&&key!="Null")yield return key;}}
    internal static List<ModelVariant> Resolve(RecordData selected,Func<string,RecordData> read){
        if(selected.Deleted)throw new InvalidDataException("Deleted winning records cannot supply model variants.");
        var result=new List<ModelVariant>();
        void Add(RecordData record,string path,string pose,string sex,int? weight,bool derived,JsonElement fields){
            string safe=CatalogueEngine.SafeAsset(path)??throw new InvalidDataException("Unsafe model path in selected record.");
            if(!safe.EndsWith(".nif",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Expected a NIF model path.");
            string slots=Text(At(fields,"BodyTemplate","FirstPersonFlags"));string id=record.Key+"|"+pose+"|"+sex+"|"+(weight?.ToString()??"authored");
            var model=sex==""?At(fields,"Model"):At(fields,"WorldModel",sex);
            if(At(model,"Model").ValueKind==JsonValueKind.Object)model=At(model,"Model");
            var alternates=At(model,"AlternateTextures");
            result.Add(new(){id=id,label=pose+" / "+(sex==""?record.Name??record.EditorId??record.Key:sex)+(weight.HasValue?" / Weight "+weight:"")+" / "+record.Key,record=record.Key,path=safe,pose=pose,sex=sex,weight=weight,derivedWeightPath=derived,slots=slots,races=Links(At(fields,"Race")).Concat(Links(At(fields,"AdditionalRaces"))).Distinct().ToArray(),priority=Text(At(fields,"Priority",sex)),textureSwaps=At(fields,"TextureSwapList").ValueKind==JsonValueKind.Undefined?null:At(fields,"TextureSwapList").Clone(),alternateTextures=alternates.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null?JsonSerializer.SerializeToElement(Array.Empty<object>()):alternates.Clone()});
            if(result.Count>32)throw new InvalidDataException("Model selection exceeds the 32-variant preview budget.");
        }
        void Gendered(RecordData record,bool worn){
            if(record.Deleted||worn&&record.Type!="ArmorAddon")throw new InvalidDataException("Expected a live ArmorAddon record.");
            var f=Fields(record);
            foreach(string sex in new[]{"Male","Female"}){
                string path=File(At(f,"WorldModel",sex));if(path=="")continue;
                bool weighted=worn&&At(f,"WeightSliderEnabled",sex).ValueKind==JsonValueKind.True&&path.EndsWith("_1.nif",StringComparison.OrdinalIgnoreCase);
                Add(record,path,worn?"Worn reference pose":"Dropped model",sex,weighted?100:null,false,f);
                if(weighted)Add(record,path[..^6]+"_0.nif","Worn reference pose",sex,0,true,f);
            }
        }
        if(selected.Type=="Armor"){
            Gendered(selected,false);var links=Links(At(Fields(selected),"Armature")).Distinct().ToArray();
            if(links.Length>16)throw new InvalidDataException("Armor exceeds the 16-addon preview budget.");
            foreach(var key in links)Gendered(read(key),true);
        }else if(selected.Type=="ArmorAddon")Gendered(selected,true);
        else {string path=selected.Assets.FirstOrDefault(x=>x.EndsWith(".nif",StringComparison.OrdinalIgnoreCase))??throw new InvalidDataException("No direct model in the selected record.");Add(selected,path,"Static","",null,false,Fields(selected));}
        if(result.Count==0)throw new InvalidDataException("The selected record has no supported model variant.");return result;
    }
    internal static ModelVariant Select(List<ModelVariant> variants,string id)=>variants.SingleOrDefault(x=>x.id==id)??throw new InvalidDataException("Model variant is not part of this selected record.");
}
internal sealed partial class CatalogueEngine {
    RecordData FreshModelRecord(string key){var source=JsonSerializer.SerializeToElement(SourceRecord(key));return source.GetProperty("record").Deserialize<RecordData>()??throw new InvalidDataException("Selected source record is unavailable.");}
    List<ModelVariant> ResolveVariants(string key)=>ModelVariants.Resolve(FreshModelRecord(key),FreshModelRecord);
    object ModelOptions(string key){
        var options=ResolveVariants(key);long bytes=0;
        foreach(var row in options){try{if(bytes>=64*1024*1024)throw new InvalidDataException("Variant asset lookup budget reached; select fewer armor components.");var asset=ReadAsset(row.path,16*1024*1024);bytes+=asset.bytes.Length;row.source=asset.source;row.available=true;}catch(Exception e){row.error=e.GetBaseException().Message;}}
        return new{key,options,note="Dropped models and worn components are separate. Worn previews use the file's reference pose and selected sex/weight endpoint. They do not reproduce your character, animation, body morphs or assembled outfit. Race links, slots and priority describe the addon; they do not verify character compatibility. Weight-zero filenames are checked against actual assets."};
    }
}
