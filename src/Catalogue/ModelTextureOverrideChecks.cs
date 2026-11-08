using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NiflySharp;
using NiflySharp.Blocks;

namespace ScriptDesk.Catalogue;
internal static class ModelTextureOverrideChecks {
    internal static void Run(Action<bool,string> check){
        JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value);
        JsonElement Rows(params object[] values)=>Json(values);
        object Row(int index=0,string key="000201:Test.esm")=>new{Name="Authored name",Index=index,NewTexture=new{FormKey=key}};
        var texture=new RecordData{Key="000201:Test.esm",Type="TextureSet",Fields=new(){["Diffuse"]=new{DataRelativePath=@"Textures\red.dds"},["NormalOrGloss"]=new{DataRelativePath=@"Textures\red_n.dds"},["EnvironmentMaskOrSubsurfaceTint"]=new{DataRelativePath=@"Textures\mask.dds"},["GlowOrDetailMap"]=new{DataRelativePath=@"Textures\glow.dds"}}};
        int reads=0;RecordData Read(string key){reads++;return texture;}
        check(ModelTextureOverrides.Resolve(Rows(),Read).Count==0&&reads==0,"Models without alternate textures do not read unrelated texture sets");
        var rules=ModelTextureOverrides.Resolve(Rows(Row(),Row(2)),Read);
        check(reads==1&&rules.Count==2&&rules[0].index==0&&rules[1].index==2,"Repeated texture-set links are resolved once while distinct shape indexes are retained");
        check(rules[0].textures[2].EndsWith("glow.dds")&&rules[0].textures[5].EndsWith("mask.dds"),"TXST glow and environment-mask fields map to NIF slots rather than record order");
        string[] original={"old diffuse","old normal","old glow","old height","old cube","old mask","old multi","old backlight","untouched ninth slot"};var replaced=ModelTextureOverrides.Apply(original,rules[0]);
        check(replaced[0].EndsWith("red.dds")&&replaced[3]==""&&replaced[8]==original[8]&&original[0]=="old diffuse","Override replaces eight slots including empty entries without mutating the NIF source array");
        void Reject(JsonElement rows,Func<string,RecordData> read,string message){bool failed=false;try{ModelTextureOverrides.Resolve(rows,read);}catch(InvalidDataException){failed=true;}check(failed,message);}
        Reject(Rows(Row(),Row()),Read,"Duplicate shape indexes cannot silently choose a texture set");
        Reject(Rows(Row(-1)),Read,"Negative alternate-texture index is rejected");
        Reject(Rows(Row(4096)),Read,"Oversized alternate-texture index is rejected");
        Reject(Json("[limited to 64]"),Read,"Truncated or non-array alternate-texture metadata is not mistaken for no overrides");
        Reject(Rows(Enumerable.Range(0,65).Select(i=>Row(i)).ToArray()),Read,"Alternate-texture list is bounded before resolving records");
        Reject(Rows(Row()),_=>new RecordData{Key=texture.Key,Type="Armor"},"Replacement link must be a TextureSet record");
        Reject(Rows(Row()),_=>new RecordData{Key=texture.Key,Type="TextureSet",Deleted=true},"Deleted winning texture set is rejected");
        Reject(Rows(Row()),_=>new RecordData{Key="000202:Test.esm",Type="TextureSet"},"Texture-set resolution must retain the exact requested identity");
        var unsafeSet=new RecordData{Key=texture.Key,Type="TextureSet",Fields=new(){["Diffuse"]="../outside.dds"}};
        Reject(Rows(Row()),_=>unsafeSet,"Texture overrides cannot escape the local asset resolver");
        unsafeSet.Fields["Diffuse"]="armor/not-a-texture.nif";Reject(Rows(Row()),_=>unsafeSet,"A NIF path cannot become a texture override");
        var alternate=new AlternateTexture{Name="Authored name",Index=3};alternate.NewTexture.SetTo(FormKey.Factory(texture.Key));
        var weapon=new Weapon(FormKey.Factory("000200:Test.esm"),SkyrimRelease.SkyrimSE){Model=new Model{File=@"Weapons\test.nif",AlternateTextures=new(){alternate}}};
        var record=RecordData.Read(weapon)!;var variant=ModelVariants.Resolve(record,_=>throw new Exception("No addon expected")).Single();
        check(variant.alternateTextures[0].GetProperty("Index").GetInt32()==3&&variant.alternateTextures[0].GetProperty("NewTexture").GetProperty("FormKey").GetString()==texture.Key,"Production record projection and static model choice retain alternate texture identity and index");
        var male=new Model{File=@"Armor\male_1.nif",AlternateTextures=new(){alternate}};var female=new Model{File=@"Armor\female_1.nif"};
        var addon=new ArmorAddon(FormKey.Factory("000203:Test.esm"),SkyrimRelease.SkyrimSE){WorldModel=new Mutagen.Bethesda.Plugins.Records.GenderedItem<Model?>(male,female)};addon.WeightSliderEnabled.Male=true;
        var variants=ModelVariants.Resolve(RecordData.Read(addon)!,_=>throw new Exception("No addon expected"));
        check(variants.Where(x=>x.sex=="Male").Count()==2&&variants.Where(x=>x.sex=="Male").All(x=>x.alternateTextures.GetArrayLength()==1)&&variants.Where(x=>x.sex=="Female").All(x=>x.alternateTextures.GetArrayLength()==0),"Worn sex and weight variants retain only their own model texture replacements");
        var nif=new NifFile(NiVersion.GetSSE(),true);var root=nif.GetRootNode();var first=new BSTriShape{Flags_ui=1};var last=new BSTriShape();var branch=new NiNode();var particle=new NiParticleSystem();
        int lastId=nif.AddBlock(last),firstId=nif.AddBlock(first),branchId=nif.AddBlock(branch),particleId=nif.AddBlock(particle);nif.AddBlock(new BSTriShape());
        root.Children.AddBlockRef(firstId);root.Children.AddBlockRef(particleId);root.Children.AddBlockRef(branchId);branch.Children.AddBlockRef(lastId);
        var indices=ModelTextureOverrides.ShapeIndices(nif);
        check(indices[first]==0&&indices[last]==2&&indices.Count==2,"Root traversal counts hidden shapes and particle systems instead of block or visible-row order");
        branch.Children.AddBlockRef(firstId);indices=ModelTextureOverrides.ShapeIndices(nif);
        check(indices.Count==2&&indices[last]==2,"Shared graph references do not duplicate or renumber shape indexes");
    }
}
