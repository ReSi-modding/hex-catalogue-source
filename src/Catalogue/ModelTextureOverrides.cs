using System.Text.Json;
using NiflySharp;
using NiflySharp.Blocks;

namespace ScriptDesk.Catalogue;
internal sealed record ModelTextureOverride(int index,string name,string textureSet,string[] textures);
internal static class ModelTextureOverrides {
    // TXST field order differs from BSShaderTextureSet slots (notably glow and
    // environment mask). These are NIF slot order, not subrecord order.
    static readonly string[] Slots={"Diffuse","NormalOrGloss","GlowOrDetailMap","Height","Environment","EnvironmentMaskOrSubsurfaceTint","Multilayer","BacklightMaskOrSpecular"};
    internal static List<ModelTextureOverride> Resolve(JsonElement rows,Func<string,RecordData> read){
        if(rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>64)throw new InvalidDataException("Invalid or oversized model alternate-texture list.");
        var result=new List<ModelTextureOverride>();var cached=new Dictionary<string,string[]>(StringComparer.OrdinalIgnoreCase);
        foreach(var row in rows.EnumerateArray()){
            if(row.ValueKind!=JsonValueKind.Object||!row.TryGetProperty("Index",out var i)||!i.TryGetInt32(out int index)||index<0||index>4095)throw new InvalidDataException("Invalid alternate-texture shape index.");
            if(result.Any(x=>x.index==index))throw new InvalidDataException("Ambiguous duplicate alternate-texture shape index.");
            string name=Text(row,"Name");if(name.Length>1024||name.Any(char.IsControl))throw new InvalidDataException("Invalid alternate-texture shape name.");
            string key=row.TryGetProperty("NewTexture",out var link)?Text(link,"FormKey"):"";
            if(key==""||key=="Null")throw new InvalidDataException("Alternate texture has no texture-set identity.");
            if(!cached.TryGetValue(key,out var paths)){
                var data=read(key);
                if(!data.Key.Equals(key,StringComparison.OrdinalIgnoreCase)||data.Type!="TextureSet"||data.Deleted)throw new InvalidDataException("Alternate texture must resolve to its live indexed TextureSet.");
                var fields=JsonSerializer.SerializeToElement(data.Fields);paths=Slots.Select(slot=>Path(fields,slot)).ToArray();cached[key]=paths;
            }
            result.Add(new(index,name,key,paths));
        }
        return result;
    }
    static string Text(JsonElement e,string key)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()!:"";
    static string Path(JsonElement fields,string name){
        if(!fields.TryGetProperty(name,out var v)||v.ValueKind==JsonValueKind.Null)return "";
        string path=v.ValueKind==JsonValueKind.String?v.GetString()!:Text(v,"DataRelativePath");
        if(path==""){
            // An empty real asset string means that the replacement clears a slot.
            if(v.ValueKind==JsonValueKind.String||v.ValueKind==JsonValueKind.Object&&Text(v,"RawPath")==""&&v.TryGetProperty("RawPath",out _))return "";
            throw new InvalidDataException("Unrecognized alternate texture path: "+name);
        }
        var safe=CatalogueEngine.SafeAsset(path);if(safe==null||!safe.EndsWith(".dds",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Unsafe or unsupported alternate texture path: "+name);
        return safe;
    }
    // Skyrim's 3D index follows root traversal, counts hidden shapes and particle
    // systems, and is not the NIF block number or the filtered preview row number.
    internal static Dictionary<INiObject,int> ShapeIndices(NifFile file){
        var result=new Dictionary<INiObject,int>();var seen=new HashSet<INiObject>();int index=0;
        void Visit(INiObject? item,int depth){
            if(item==null||!seen.Add(item))return;
            if(depth>128||seen.Count>50000)throw new InvalidDataException("Alternate-texture graph exceeds the traversal budget.");
            // NiflySharp also implements INiShape on particle systems; they
            // consume an index but must not become a texture replacement target.
            if(item is NiParticleSystem)index++;
            else if(item is INiShape)result[item]=index++;
            foreach(var child in item.References)if(child!=null)Visit(file.GetBlock<INiObject>(child),depth+1);
        }
        Visit(file.GetRootNode(),0);return result;
    }
    internal static string[] Apply(string[] original,ModelTextureOverride change){
        if(change.textures.Length!=8)throw new InvalidDataException("Expected eight replacement texture slots.");
        var replaced=new string[Math.Max(8,original.Length)];Array.Copy(original,replaced,original.Length);Array.Copy(change.textures,replaced,8);return replaced;
    }
}
