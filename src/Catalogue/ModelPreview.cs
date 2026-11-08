using System.Numerics;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;

namespace ScriptDesk.Catalogue;
internal sealed partial class CatalogueEngine {
    (byte[] bytes,string source) ReadAsset(string raw,int limit){
        string relative=SafeAsset(raw)??throw new InvalidDataException("Unsafe asset path.");string path=Path.GetFullPath(Path.Combine(dataPath,relative));
        // Reject reparse-point escapes for every existing ancestor, including the Data root.
        for(string? p=path;p!=null;p=Path.GetDirectoryName(p))if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Preview does not follow reparse points.");
        if(File.Exists(path)){if(new FileInfo(path).Length>limit)throw new InvalidDataException("Asset size exceeds preview budget.");return(File.ReadAllBytes(path),"Loose: "+relative);}
        var candidates=new List<(string name,IArchiveFile file)>();
        foreach(string archivePath in Directory.EnumerateFiles(dataPath,"*.bsa")){if((File.GetAttributes(archivePath)&FileAttributes.ReparsePoint)!=0)continue;var a=Archive.CreateReader(GameRelease.SkyrimSE,archivePath);var f=a.Files.FirstOrDefault(x=>x.Path.Replace('/','\\').Equals(relative,StringComparison.OrdinalIgnoreCase));if(f!=null)candidates.Add((Path.GetFileName(archivePath),f));}
        if(candidates.Count!=1)throw new InvalidDataException(candidates.Count==0?"Asset not found: "+relative:"Multiple archive candidates; mounted priority is unresolved: "+relative);
        if(candidates[0].file.Size>limit)throw new InvalidDataException("Archive asset exceeds preview budget.");byte[] bytes=candidates[0].file.GetBytes();if(bytes.Length>limit)throw new InvalidDataException("Expanded asset exceeds preview budget.");return(bytes,candidates[0].name+": "+relative);
    }
    object Model(string key,string variant=""){
        var choices=ResolveVariants(key);var choice=variant==""?choices[0]:ModelVariants.Select(choices,variant);string path=choice.path;
        var overrides=ModelTextureOverrides.Resolve(choice.alternateTextures,FreshModelRecord);
        bool bindPose=choice.pose=="Worn reference pose";
        var input=ReadAsset(path,16*1024*1024);var textures=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
        object Texture(string raw,NifMaterial material,bool glow,bool mask){
            string cacheKey=raw+"|"+JsonSerializer.Serialize(material)+"|"+glow+"|"+mask;
            if(textures.TryGetValue(cacheKey,out var cached))return cached;
            if(textures.Count>=16)return new{path=raw,error="Sixteen-texture preview limit"};
            object result;try{var a=ReadAsset(raw,16*1024*1024);result=new{path=raw,source=a.source,image=Convert.ToBase64String(mask?TexturePreview.DecodeSpecularMask(a.bytes):TexturePreview.DecodeModel(a.bytes,material,glow))};}
            catch(Exception e){result=new{path=raw,error=e.GetBaseException().Message};}
            textures[cacheKey]=result;return result;
        }
        var geometry=NifGeometry.Read(input.bytes,(raw,material,glow)=>Texture(raw,material,glow,false),bindPose,overrides,(raw,material)=>Texture(raw,material,false,true));
        bool EnablePendingWeightPreview=false; // Preserved for P2 validation; not shipped as a working feature.
        object? weightBlend=null;
        if(EnablePendingWeightPreview&&bindPose&&choice.weight.HasValue){
            try{
                var other=choices.Single(x=>x.record==choice.record&&x.pose==choice.pose&&x.sex==choice.sex&&x.weight==100-choice.weight.Value);
                var otherAsset=ReadAsset(other.path,16*1024*1024);
                var otherOverrides=ModelTextureOverrides.Resolve(other.alternateTextures,FreshModelRecord);
                var otherGeometry=NifGeometry.Read(otherAsset.bytes,(_,_,_)=>null!,true,otherOverrides);
                weightBlend=new{pair=ModelWeightPair.Read(geometry,otherGeometry,choice.weight.Value),source=otherAsset.source,path=other.path};
            }catch(Exception e){weightBlend=new{pair=new{available=false,reason=e.GetBaseException().Message}};}
        }
        return new{key,source=input.source,geometry,variant=choice,weightBlend,note=bindPose?"Armor component in its authored reference pose; compatible weight endpoints can be interpolated locally. This is not your equipped character. No animation, custom body morphs, outfit composition or race skin-texture substitution. Material limitations are reported per shape.":"Static NIF with model alternate textures, authored normals, diffuse, material alpha, specular color and emission where supported. Neutral lighting; WPF shading is approximate. Normal maps, reflections and animation are not rendered. Per-shape limitations are reported below."};
    }
}
internal static class NifGeometry {
    internal static bool Hidden(uint flags,IEnumerable<uint> parents){return (flags&1)!=0||parents.Any(f=>(f&1)!=0);}
    internal static Vector3 Transform(Vector3 v,Matrix33 m,float scale,Vector3 offset){return new Vector3(m.M11*v.X+m.M12*v.Y+m.M13*v.Z,m.M21*v.X+m.M22*v.Y+m.M23*v.Z,m.M31*v.X+m.M32*v.Y+m.M33*v.Z)*scale+offset;}
    internal static void Validate(float[] vertices,int[] indices){if(vertices.Length<9||vertices.Length%3!=0||vertices.Length>180000||indices.Length%3!=0||indices.Length>300000||vertices.Any(v=>!float.IsFinite(v)||Math.Abs(v)>1e7f)||indices.Any(i=>i<0||i>=vertices.Length/3))throw new InvalidDataException("Invalid or oversized mesh geometry.");}
    static Matrix33 Cofactors(Matrix33 m)=>new(){M11=m.M22*m.M33-m.M23*m.M32,M12=m.M23*m.M31-m.M21*m.M33,M13=m.M21*m.M32-m.M22*m.M31,M21=m.M13*m.M32-m.M12*m.M33,M22=m.M11*m.M33-m.M13*m.M31,M23=m.M12*m.M31-m.M11*m.M32,M31=m.M12*m.M23-m.M13*m.M22,M32=m.M13*m.M21-m.M11*m.M23,M33=m.M11*m.M22-m.M12*m.M21};
    static float Determinant(Matrix33 m){var c=Cofactors(m);return m.M11*c.M11+m.M12*c.M12+m.M13*c.M13;}
    internal static int Handedness(Matrix33 matrix,float scale){
        var entries=new[]{matrix.M11,matrix.M12,matrix.M13,matrix.M21,matrix.M22,matrix.M23,matrix.M31,matrix.M32,matrix.M33};float det=Determinant(matrix);
        if(entries.Any(v=>!float.IsFinite(v))||!float.IsFinite(scale)||Math.Abs(scale)<1e-8f||!float.IsFinite(det)||Math.Abs(det)<1e-8f)throw new InvalidDataException("Singular or nonfinite mesh transform.");
        return Math.Sign(det)*Math.Sign(scale);
    }
    internal static void OrientTriangles(int[] indices,int handedness){if(handedness<0)for(int i=0;i<indices.Length;i+=3)(indices[i+1],indices[i+2])=(indices[i+2],indices[i+1]);}
    internal static Vector3 Normal(Vector3 v,Matrix33 rotation,float scale){
        int direction=Handedness(rotation,scale);
        // Cofactors are determinant * inverse-transpose. Normalization removes its
        // magnitude; retain determinant/scale signs for reflected transforms.
        var n=Transform(v,Cofactors(rotation),direction,Vector3.Zero);
        if(!float.IsFinite(n.LengthSquared())||n.LengthSquared()<1e-12f)throw new InvalidDataException("Invalid authored normal.");return Vector3.Normalize(n);
    }
    internal static (Matrix33 rotation,float scale,Vector3 translation) Inverse(Matrix33 matrix,float scale,Vector3 offset){
        Handedness(matrix,scale);if(!float.IsFinite(offset.X)||!float.IsFinite(offset.Y)||!float.IsFinite(offset.Z))throw new InvalidDataException("Nonfinite skin translation.");
        var c=Cofactors(matrix);float d=Determinant(matrix);var inverse=new Matrix33{M11=c.M11/d,M12=c.M21/d,M13=c.M31/d,M21=c.M12/d,M22=c.M22/d,M23=c.M32/d,M31=c.M13/d,M32=c.M23/d,M33=c.M33/d};
        float inverseScale=1/scale;return(inverse,inverseScale,-Transform(offset,inverse,inverseScale,Vector3.Zero));
    }
    internal static object Read(byte[] input,Func<string,NifMaterial,bool,object> texture,bool bindPose=false,IReadOnlyList<ModelTextureOverride>? overrides=null,Func<string,NifMaterial,object>? specularMask=null){
        if(input.Length<80||input.Length>16*1024*1024)throw new InvalidDataException("Invalid NIF size.");
        if(!System.Text.Encoding.ASCII.GetString(input,0,Math.Min(input.Length,80)).StartsWith("Gamebryo File Format"))throw new InvalidDataException("Expected Skyrim Gamebryo NIF.");
        using var stream=new MemoryStream(input,false);var file=new NifFile(stream);if(!file.Valid)throw new InvalidDataException("NIF parser rejected this model.");
        overrides??=Array.Empty<ModelTextureOverride>();var shapeIndices=overrides.Count>0?ModelTextureOverrides.ShapeIndices(file):new Dictionary<INiObject,int>();var appliedOverrides=new HashSet<int>();
        var shapes=new List<object>();var warnings=new List<string>();int vertexTotal=0,triangleTotal=0;
        foreach(var shape in file.GetShapes().Take(65)){
            if(shapes.Count>=64)throw new InvalidDataException("Too many mesh shapes.");if(shape.IsSkinned&&!bindPose){warnings.Add("Skipped skinned shape: "+shape.Name);continue;}
            var points=shape is BSTriShape bs?bs.VertexPositions:shape.GeometryData?.Vertices;var uv=shape is BSTriShape b?b.UVs:shape.GeometryData?.UVSets;
            if(points==null||points.Count==0){warnings.Add("No static vertices: "+shape.Name);continue;}
            var parents=new List<NiNode>();var parent=file.GetParentNode(shape);while(parent!=null){if(parents.Count>=32||parents.Contains(parent))throw new InvalidDataException("Invalid NIF parent graph.");parents.Add(parent);parent=file.GetParentNode(parent);}
            if(Hidden(((NiAVObject)shape).Flags_ui,parents.Select(n=>n.Flags_ui))){warnings.Add("Skipped hidden shape: "+shape.Name.String);continue;}
            var transform=(rotation:shape.Rotation,scale:shape.Scale,translation:shape.Translation);object? skinInfo=null;bool referencePose=shape.IsSkinned&&bindPose;
            if(referencePose){
                var instance=file.GetBlock(shape.SkinInstanceRef) as NiSkinInstance;
                var skinData=instance==null?null:file.GetBlock(instance.Data) as NiSkinData;
                var root=instance==null?null:file.GetBlock(instance.SkeletonRoot) as NiNode;
                if(instance==null||skinData==null||root==null||instance.NumBones<1||instance.NumBones>256||skinData.BoneList.Count!=instance.NumBones)throw new InvalidDataException("Worn preview requires complete Skyrim NiSkinData and a skeleton root.");
                var t=skinData.SkinTransform;transform=Inverse(t.Rotation,t.Scale,t.Translation);
                skinInfo=new{type=instance.GetType().Name,root=root.Name.String,bones=instance.NumBones,pose="Authored bind pose; no animated bone transforms"};
                warnings.Add(shape.Name.String+": Reference pose only; current skeleton, animation and body morphs are not applied.");
            }
            int handedness=Handedness(transform.rotation,transform.scale);if(!referencePose)foreach(var node in parents)handedness*=Handedness(node.Rotation,node.Scale);
            var vertices=new List<float>();foreach(var point in points){var v=Transform(point,transform.rotation,transform.scale,transform.translation);if(!referencePose)foreach(var node in parents)v=Transform(v,node.Rotation,node.Scale,node.Translation);vertices.AddRange(new[]{v.X,v.Y,v.Z});}
            var indices=shape.Triangles.SelectMany(t=>new[]{(int)t.V1,(int)t.V2,(int)t.V3}).ToArray();var positions=vertices.ToArray();Validate(positions,indices);vertexTotal+=positions.Length/3;triangleTotal+=indices.Length/3;if(vertexTotal>60000||triangleTotal>100000)throw new InvalidDataException("Model exceeds the 60k vertex / 100k triangle budget.");
            OrientTriangles(indices,handedness);
            var shader=file.GetShader(shape);var textureSet=shader?.TextureSetRef==null?null:file.GetBlock(shader.TextureSetRef);var paths=textureSet?.Textures?.Select(t=>t.Content??"").Take(12).ToArray()??Array.Empty<string>();
            object? textureOverride=null;
            if(shapeIndices.TryGetValue(shape,out int shapeIndex)){
                var change=overrides.SingleOrDefault(x=>x.index==shapeIndex);
                if(change!=null){var originalPaths=paths;paths=ModelTextureOverrides.Apply(paths,change);appliedOverrides.Add(shapeIndex);textureOverride=new{index=shapeIndex,recordName=change.name,change.textureSet,originalTextures=originalPaths};
                    warnings.Add(shape.Name.String+": Applied model alternate texture "+change.textureSet+" at 3D index "+shapeIndex+(change.name!=shape.Name.String?" (record name: "+change.name+")":"")+".");}
            }
            var alpha=shape.HasAlphaProperty?file.GetBlock(shape.AlphaPropertyRef):null;var material=NifMaterial.Read(shader,alpha,paths);warnings.AddRange(material.limitations.Select(w=>shape.Name.String+": "+w));
            float[] coords=uv==null?Array.Empty<float>():uv.SelectMany(t=>new[]{t.U*material.uvScale[0]+material.uvOffset[0],t.V*material.uvScale[1]+material.uvOffset[1]}).ToArray();if(coords.Any(v=>!float.IsFinite(v)))coords=Array.Empty<float>();
            var normals=new List<float>();var sourceNormals=shape.HasNormals?(shape is BSTriShape normalShape?normalShape.Normals:shape.GeometryData?.Normals):null;
            if(sourceNormals!=null&&sourceNormals.Count==points.Count){try{foreach(var point in sourceNormals){var n=Normal(point,transform.rotation,transform.scale);if(!referencePose)foreach(var node in parents)n=Normal(n,node.Rotation,node.Scale);normals.AddRange(new[]{n.X,n.Y,n.Z});}}catch(InvalidDataException){normals.Clear();warnings.Add(shape.Name.String+": Invalid normals; geometric normals used.");}}
            else warnings.Add(shape.Name.String+": No complete authored normals; geometric normals used.");
            shapes.Add(new{name=shape.Name.String,vertices=positions,triangles=indices,uv=coords,normals=normals.ToArray(),skin=skinInfo,material,textures=paths,textureOverride,
                diffuse=paths.Length>0&&!String.IsNullOrWhiteSpace(paths[0])?texture(paths[0],material,false):null,
                specularMask=material.normalAlphaSpecular&&specularMask!=null?specularMask(paths[1],material):null,
                glow=material.glowMap&&paths.Length>2&&!String.IsNullOrWhiteSpace(paths[2])?texture(paths[2],material,true):null});
        }
        foreach(var change in overrides.Where(x=>!appliedOverrides.Contains(x.index)))warnings.Add("Alternate texture "+change.textureSet+" at 3D index "+change.index+" was not applied to a visible supported shape; its original target may be hidden, unsupported or absent.");
        if(shapes.Count==0)throw new InvalidDataException("No supported mesh shapes for the selected preview mode.");
        return new{shapes,vertexTotal,triangleTotal,warnings,unknownBlocks=file.HasUnknownBlocks};
    }
}
