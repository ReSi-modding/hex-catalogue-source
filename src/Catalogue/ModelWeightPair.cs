using System.Text.Json;

namespace ScriptDesk.Catalogue;
internal static class ModelWeightPair {
    // Only authored endpoint pairs with the same visible topology, UVs and
    // materials can share the desktop slider. Never interpolate unrelated NIFs.
    internal static object Read(object primary,object alternate,int primaryWeight){
        if(primaryWeight is not (0 or 100))throw new InvalidDataException("Weight endpoint must be 0 or 100.");
        var a=JsonSerializer.SerializeToElement(primary).GetProperty("shapes").EnumerateArray().ToArray();
        var b=JsonSerializer.SerializeToElement(alternate).GetProperty("shapes").EnumerateArray().ToArray();
        if(a.Length==0||a.Length>64||a.Length!=b.Length)throw new InvalidDataException("Weight models have different visible shape counts.");
        var rows=new List<object>();int vertices=0;
        for(int i=0;i<a.Length;i++){
            foreach(string field in new[]{"name","triangles","uv","textures","material","skin"})
                if(!Same(a[i],b[i],field))throw new InvalidDataException("Weight models differ at shape "+i+" ("+field+"); endpoint preview retained.");
            var first=Floats(a[i],"vertices");var other=Floats(b[i],"vertices");
            if(first.Length!=other.Length||first.Length<9||first.Length%3!=0||first.Length>180000)throw new InvalidDataException("Weight models have incompatible vertices.");
            vertices+=first.Length/3;if(vertices>60000)throw new InvalidDataException("Weight-pair vertex budget exceeded.");
            var normalsA=Floats(a[i],"normals");var normalsB=Floats(b[i],"normals");
            bool authored=normalsA.Length==first.Length&&normalsB.Length==first.Length;
            rows.Add(new{vertices=other,normals=authored?normalsB:Array.Empty<float>(),useAuthoredNormals=authored});
        }
        return new{available=true,primaryWeight,alternateWeight=100-primaryWeight,shapes=rows,vertexCount=vertices,note="Linear interpolation of compatible authored weight endpoints in reference pose. Does not apply current character skeleton, custom body morphs or animation."};
    }
    static bool Same(JsonElement a,JsonElement b,string field){
        bool left=a.TryGetProperty(field,out var x),right=b.TryGetProperty(field,out var y);
        return left==right&&(!left||x.GetRawText()==y.GetRawText());
    }
    static float[] Floats(JsonElement row,string field){
        if(!row.TryGetProperty(field,out var value)||value.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("Missing weight geometry field: "+field);
        var values=value.EnumerateArray().Select(x=>x.GetSingle()).ToArray();
        if(values.Any(x=>!float.IsFinite(x)||Math.Abs(x)>1e7f))throw new InvalidDataException("Invalid weight geometry values.");return values;
    }
}
