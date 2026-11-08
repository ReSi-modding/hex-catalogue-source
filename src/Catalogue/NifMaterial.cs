using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Enums;

namespace ScriptDesk.Catalogue;
// Explicit preview subset. Do not infer transparency from a DDS alpha channel.
internal sealed class NifMaterial {
    public string shader {get;init;}="Unknown";
    public bool supported {get;init;}
    public bool doubleSided {get;init;}=true;
    public bool alphaBlend {get;init;}
    public bool alphaTest {get;init;}
    public int alphaFunction {get;init;}
    public byte alphaThreshold {get;init;}
    public string sourceBlend {get;init;}="ONE";
    public string destinationBlend {get;init;}="ZERO";
    public bool blendSupported {get;init;}=true;
    public float opacity {get;init;}=1;
    public bool specular {get;init;}
    public bool normalAlphaSpecular {get;init;}
    public float[] specularColor {get;init;}=new float[]{0,0,0};
    public float specularStrength {get;init;}
    public float glossiness {get;init;}
    public bool emissive {get;init;}
    public float[] emissiveColor {get;init;}=new float[]{0,0,0};
    public float emissiveMultiple {get;init;}
    public bool glowMap {get;init;}
    public float[] uvScale {get;init;}=new float[]{1,1};
    public float[] uvOffset {get;init;}=new float[]{0,0};
    public string[] limitations {get;init;}=Array.Empty<string>();
    public EffectMaterialInfo? effect {get;init;}
    static float Finite(float value,float fallback,float min,float max)=>float.IsFinite(value)?Math.Clamp(value,min,max):fallback;
    internal static NifMaterial Read(INiShader? s,NiAlphaProperty? a,string[] textures){
        if(s is BSEffectShaderProperty effectShader&&effectShader.Type==NiflySharp.Helpers.ShaderHelper.ShaderGameType.SK){
            var info=EffectMaterialInfo.Read(effectShader);var alpha=a?.Flags;
            var details=new List<string>{"Effect shader inputs inspected; neutral diffuse fallback. Palette, vertex color/alpha, falloff and authored effect opacity are not rendered."};
            if(info.invalidFields.Length>0)details.Add("Invalid effect fields are reported as unavailable: "+string.Join(", ",info.invalidFields));
            return new(){shader=s.GetType().Name,effect=info,alphaBlend=alpha?.AlphaBlend??false,alphaTest=alpha?.AlphaTest??false,
                alphaFunction=(int)(alpha?.TestFunc??TestFunction.TEST_ALWAYS),alphaThreshold=a?.Threshold??0,
                sourceBlend=alpha?.SourceBlendMode.ToString()??"ONE",destinationBlend=alpha?.DestinationBlendMode.ToString()??"ZERO",
                blendSupported=false,limitations=details.ToArray()};
        }
        if(s is not BSLightingShaderProperty lighting||lighting.Type!=NiflySharp.Helpers.ShaderHelper.ShaderGameType.SK)return new(){shader=s?.GetType().Name??"None",limitations=new[]{"Unsupported shader: neutral diffuse fallback."}};
        var flags=a?.Flags;bool blend=flags?.AlphaBlend??false;
        bool ordinaryBlend=!blend||(flags!.SourceBlendMode==AlphaFunction.SRC_ALPHA&&flags.DestinationBlendMode==AlphaFunction.INV_SRC_ALPHA);
        var missing=new List<string>();
        if(!ordinaryBlend)missing.Add("Nonstandard alpha blend factors are not rendered; opaque fallback.");
        else if(blend)missing.Add("Translucent triangle ordering is approximate in this preview.");
        if(s.HasVertexAlpha)missing.Add("Per-vertex alpha is not rendered.");
        if(s.ShaderFlags_SSPF2.HasFlag(SkyrimShaderPropertyFlags2.Vertex_Colors))missing.Add("Per-vertex color is not rendered.");
        bool hasNormal=textures.Length>1&&!string.IsNullOrWhiteSpace(textures[1]);
        // NifSkope sk_default.frag uses normal alpha for specular intensity.
        // Model-space normals use a separate specular map; other shader paths
        // retain their existing approximation until their semantics are mapped.
        bool normalSpec=s.HasSpecular&&hasNormal&&lighting.ShaderType_SK_FO4==BSLightingShaderType.Default&&!s.ShaderFlags_SSPF1.HasFlag(SkyrimShaderPropertyFlags1.Model_Space_Normals);
        if(hasNormal)missing.Add("Normal-map surface lighting is not rendered; authored mesh normals are used.");
        if(s.HasSpecular&&hasNormal&&!normalSpec)missing.Add("This shader's specular texture is not rendered; uniform specular fallback.");
        if(s.ShaderFlags_SSPF1.HasFlag(SkyrimShaderPropertyFlags1.Environment_Mapping))missing.Add("Environment reflections are not rendered.");
        if(s.ShaderFlags_SSPF1.HasFlag(SkyrimShaderPropertyFlags1.Parallax)||s.ShaderFlags_SSPF1.HasFlag(SkyrimShaderPropertyFlags1.Parallax_Occlusion))missing.Add("Parallax is not rendered.");
        bool hasGlow=s.HasGlowmap&&s.Emissive;
        if(hasGlow&&a!=null&&(blend||flags!.AlphaTest))missing.Add("Glow-map transparency is approximated independently from the diffuse mask.");
        var spec=s.SpecularColor;var emission=s.EmissiveColor;
        return new(){shader=s.GetType().Name,supported=true,doubleSided=s.DoubleSided,alphaBlend=blend,alphaTest=flags?.AlphaTest??false,
            alphaFunction=(int)(flags?.TestFunc??TestFunction.TEST_ALWAYS),alphaThreshold=a?.Threshold??0,
            sourceBlend=flags?.SourceBlendMode.ToString()??"ONE",destinationBlend=flags?.DestinationBlendMode.ToString()??"ZERO",blendSupported=ordinaryBlend,
            opacity=Finite(s.Alpha,1,0,128),specular=s.HasSpecular,normalAlphaSpecular=normalSpec,specularColor=new[]{Finite(spec.R,0,0,128),Finite(spec.G,0,0,128),Finite(spec.B,0,0,128)},
            specularStrength=Finite(s.SpecularStrength,0,0,128),glossiness=Finite(s.Glossiness,0,0,128),emissive=s.Emissive,
            emissiveColor=new[]{Finite(emission.R,0,0,128),Finite(emission.G,0,0,128),Finite(emission.B,0,0,128)},emissiveMultiple=Finite(s.EmissiveMultiple,0,0,128),glowMap=hasGlow,
            uvScale=new[]{Finite(s.UVScale.U,1,-10000,10000),Finite(s.UVScale.V,1,-10000,10000)},uvOffset=new[]{Finite(s.UVOffset.U,0,-10000,10000),Finite(s.UVOffset.V,0,-10000,10000)},limitations=missing.ToArray()};
    }
    internal byte Alpha(byte source){
        if(!supported||!blendSupported||(!alphaTest&&!alphaBlend))return 255;
        int alpha=(int)Math.Clamp(Math.Round(source*opacity),0,255);
        bool pass=!alphaTest||alphaFunction switch{0=>true,1=>alpha<alphaThreshold,2=>alpha==alphaThreshold,3=>alpha<=alphaThreshold,4=>alpha>alphaThreshold,5=>alpha!=alphaThreshold,6=>alpha>=alphaThreshold,7=>false,_=>false};
        return !pass?(byte)0:alphaBlend?(byte)alpha:(byte)255;
    }
}
