using NiflySharp.Blocks;
using NiflySharp.Enums;

namespace ScriptDesk.Catalogue;
// Inspection only. These inputs do not imply that WPF renders the effect shader.
internal sealed class EffectMaterialInfo {
    public string sourceTexture {get;init;}="";
    public string paletteTexture {get;init;}="";
    public float?[] baseColor {get;init;}=Array.Empty<float?>();
    public float? colorScale {get;init;}
    public float?[] falloff {get;init;}=Array.Empty<float?>();
    public float? softDepth {get;init;}
    public byte clampMode {get;init;}
    public byte lightingInfluence {get;init;}
    public bool paletteColor {get;init;}
    public bool paletteAlpha {get;init;}
    public bool vertexColor {get;init;}
    public bool vertexAlpha {get;init;}
    public bool useFalloff {get;init;}
    public bool weaponBlood {get;init;}
    public uint flags1 {get;init;}
    public uint flags2 {get;init;}
    public string[] invalidFields {get;init;}=Array.Empty<string>();

    internal static EffectMaterialInfo Read(BSEffectShaderProperty s){
        var invalid=new List<string>();
        float? Number(float value,string name){if(float.IsFinite(value))return value;invalid.Add(name);return null;}
        string Path(string? value,string name){if(value==null)return "";if(value.Length>1024||value.Any(char.IsControl)){invalid.Add(name);return "";}return value;}
        return new(){sourceTexture=Path(s.SourceTexture?.Content,"sourceTexture"),paletteTexture=Path(s.GreyscaleTexture?.Content,"paletteTexture"),
            baseColor=new[]{Number(s.BaseColor.R,"baseColor.R"),Number(s.BaseColor.G,"baseColor.G"),Number(s.BaseColor.B,"baseColor.B"),Number(s.BaseColor.A,"baseColor.A")},
            colorScale=Number(s.BaseColorScale,"colorScale"),falloff=new[]{Number(s.FalloffStartAngle,"falloff.startAngle"),Number(s.FalloffStopAngle,"falloff.stopAngle"),Number(s.FalloffStartOpacity,"falloff.startOpacity"),Number(s.FalloffStopOpacity,"falloff.stopOpacity")},
            softDepth=Number(s.SoftFalloffDepth,"softDepth"),clampMode=s.TextureClampMode,lightingInfluence=s.LightingInfluence,
            paletteColor=s.ShaderFlags_SSPF1.HasFlag(SkyrimShaderPropertyFlags1.Greyscale_To_PaletteColor),paletteAlpha=s.ShaderFlags_SSPF1.HasFlag(SkyrimShaderPropertyFlags1.Greyscale_To_PaletteAlpha),
            useFalloff=s.ShaderFlags_SSPF1.HasFlag(SkyrimShaderPropertyFlags1.Use_Falloff),vertexAlpha=s.HasVertexAlpha,
            vertexColor=s.ShaderFlags_SSPF2.HasFlag(SkyrimShaderPropertyFlags2.Vertex_Colors),weaponBlood=s.ShaderFlags_SSPF2.HasFlag(SkyrimShaderPropertyFlags2.Weapon_Blood),
            flags1=(uint)s.ShaderFlags_SSPF1,flags2=(uint)s.ShaderFlags_SSPF2,invalidFields=invalid.ToArray()};
    }
}
