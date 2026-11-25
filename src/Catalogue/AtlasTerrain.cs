using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ScriptDesk.Catalogue;
internal sealed partial class CatalogueEngine {
    internal static class AtlasTerrain {
        internal static double[] Decode(byte[] bytes){
            if(bytes.Length!=1096)throw new InvalidDataException("Unsupported LAND height grid.");double offset=BitConverter.ToSingle(bytes,0);if(!double.IsFinite(offset))throw new InvalidDataException("Non-finite LAND height offset.");var values=new double[1089];double first=offset;
            for(int y=0;y<33;y++){first+=unchecked((sbyte)bytes[4+y*33]);double v=first;values[y*33]=v*8;for(int x=1;x<33;x++){v+=unchecked((sbyte)bytes[4+y*33+x]);values[y*33+x]=v*8;}}
            if(values.Any(x=>!double.IsFinite(x)||Math.Abs(x)>10000000))throw new InvalidDataException("LAND height outside bounded coordinates.");return values;
        }
        internal static Dictionary<string,object?>? Render(string world,AtlasBinary.Snapshot snapshot,Dictionary<string,object?> bounds){
            if(snapshot.Worlds.TryGetValue(world,out var owning)&&(owning.WorldFlags&16)!=0)return null;
            var cells=new Dictionary<(int x,int y),(AtlasBinary.Geo record,double[] heights)>();var conflicts=new List<object>();var unknown=new HashSet<(int x,int y)>();int excluded=0;
            var inheritance=new List<object>();var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int clipMinX=checked((int)Math.Floor(Convert.ToDouble(bounds["minX"])/4096)),clipMaxX=checked((int)Math.Ceiling(Convert.ToDouble(bounds["maxX"])/4096))-1;
            int clipMinY=checked((int)Math.Floor(Convert.ToDouble(bounds["minY"])/4096)),clipMaxY=checked((int)Math.Ceiling(Convert.ToDouble(bounds["maxY"])/4096))-1;
            void Apply(string sourceWorld){
                if(!visited.Add(sourceWorld))throw new InvalidDataException("Cyclic parent-world LAND inheritance.");
                if(snapshot.Worlds.TryGetValue(sourceWorld,out var w)&&(w.ParentFlags&1)!=0&&w.Parent!=""){
                    if(snapshot.Worlds.TryGetValue(w.Parent,out var parent)&&parent.Enabled&&(parent.WorldFlags&16)==0){inheritance.Add(new{worldFormKey=sourceWorld,parentWorldFormKey=w.Parent,parentUseFlags=w.ParentFlags,rule="Explicit PNAM bit 0 Use Land Data; parent LAND cropped to child extent. Conflicting own/parent coordinates remain unknown."});Apply(w.Parent);}
                    else inheritance.Add(new{worldFormKey=sourceWorld,parentWorldFormKey=w.Parent,parentUseFlags=w.ParentFlags,rule="Declared parent LAND source unavailable; missing relief remains unknown."});
                }
                foreach(var land in snapshot.Lands.Values.Where(l=>l.World.Equals(sourceWorld,StringComparison.OrdinalIgnoreCase)).OrderBy(l=>l.Ordinal).ThenBy(l=>l.Offset)){
                    if(!land.Enabled||land.Heights==null||!snapshot.Cells.TryGetValue(land.Cell,out var cell)||!cell.Enabled||cell.Interior||cell.Grid==null){excluded++;continue;}
                    var grid=(cell.Grid[0],cell.Grid[1]);if(sourceWorld!=world&&(grid.Item1<clipMinX||grid.Item1>clipMaxX||grid.Item2<clipMinY||grid.Item2>clipMaxY))continue;
                    if(unknown.Contains(grid))continue;
                    if(cells.TryGetValue(grid,out var previous)&&!previous.record.Key.Equals(land.Key,StringComparison.OrdinalIgnoreCase)){
                        conflicts.Add(new{grid=new[]{grid.Item1,grid.Item2},earlierLand=previous.record.Key,laterLand=land.Key,earlierSourcePlugin=previous.record.Plugin,earlierSourceOffset=previous.record.Offset,laterSourcePlugin=land.Plugin,laterSourceOffset=land.Offset,policy="Ambiguous authored LAND coordinate or own/parent overlap; relief tile is transparent unknown."});cells.Remove(grid);unknown.Add(grid);continue;
                    }
                    cells[grid]=(land,Decode(land.Heights));
                }
            }
            Apply(world);
            if(cells.Count==0)return null;
            double minX=Convert.ToDouble(bounds["minX"]),maxX=Convert.ToDouble(bounds["maxX"]),minY=Convert.ToDouble(bounds["minY"]),maxY=Convert.ToDouble(bounds["maxY"]);
            int mincx=checked((int)Math.Floor(minX/4096)),maxcx=checked((int)Math.Ceiling(maxX/4096))-1,mincy=checked((int)Math.Floor(minY/4096)),maxcy=checked((int)Math.Ceiling(maxY/4096))-1;
            // LAND coordinates, including otherwise unmarked cells, set the image
            // extent. Missing coverage stays alpha zero, never fabricated relief.
            mincx=Math.Min(mincx,cells.Keys.Min(k=>k.x));maxcx=Math.Max(maxcx,cells.Keys.Max(k=>k.x));mincy=Math.Min(mincy,cells.Keys.Min(k=>k.y));maxcy=Math.Max(maxcy,cells.Keys.Max(k=>k.y));
            int nx=checked(maxcx-mincx+1),ny=checked(maxcy-mincy+1),samples=32;while(samples>1&&(nx*samples>2048||ny*samples>2048))samples/=2;
            if(nx<=0||ny<=0||nx*samples>4096||ny*samples>4096)throw new InvalidDataException("Worldspace terrain exceeds bounded raster dimensions.");
            int width=nx*samples,height=ny*samples,step=32/samples;var rgba=new byte[checked(width*height*4)];var evidence=new List<object>();long alpha=0;double seamMax=0;int seamNonzero=0,seamSamples=0;
            foreach(var pair in cells){int cx=pair.Key.x,cy=pair.Key.y;var heights=pair.Value.heights;var land=pair.Value.record;
                for(int sy=0;sy<samples;sy++)for(int sx=0;sx<samples;sx++){
                    int x=sx*step,y=sy*step;double z=(heights[y*33+x]+heights[(y+step)*33+x]+heights[y*33+x+step]+heights[(y+step)*33+x+step])/4;
                    double dx=((heights[y*33+x+step]+heights[(y+step)*33+x+step])-(heights[y*33+x]+heights[(y+step)*33+x]))/(256*step);
                    double dy=((heights[(y+step)*33+x]+heights[(y+step)*33+x+step])-(heights[y*33+x]+heights[y*33+x+step]))/(256*step);
                    double light=Math.Max(0,(-dx*-.55-dy*.55+.63)/Math.Sqrt(dx*dx+dy*dy+1)),shade=.40+.60*light,t=Math.Clamp((z+12000)/34000,0,1);
                    int px=(cx-mincx)*samples+sx,py=(maxcy-cy)*samples+(samples-1-sy),at=(py*width+px)*4;int[] low={72,85,83},high={202,205,197};for(int c=0;c<3;c++)rgba[at+c]=(byte)Math.Clamp((int)Math.Round((low[c]+t*(high[c]-low[c]))*shade),0,255);rgba[at+3]=255;alpha++;
                }
                void Seam(double v){seamSamples++;if(v!=0)seamNonzero++;seamMax=Math.Max(seamMax,v);}
                if(cells.TryGetValue((cx+1,cy),out var east))for(int y=0;y<33;y++)Seam(Math.Abs(heights[y*33+32]-east.heights[y*33]));
                if(cells.TryGetValue((cx,cy+1),out var north))for(int x=0;x<33;x++)Seam(Math.Abs(heights[32*33+x]-north.heights[x]));
                evidence.Add(new{id=land.Key,cellFormKey=land.Cell,grid=new[]{cx,cy},sourceWorldFormKey=land.World,inherited=!land.World.Equals(world,StringComparison.OrdinalIgnoreCase),sourcePlugin=land.Plugin,sourceOffset=land.Offset,recordFlags=land.Flags,vhgtSha256=Convert.ToHexString(SHA256.HashData(land.Heights!)).ToLowerInvariant(),heightRange=new[]{heights.Min(),heights.Max()},versions=land.Versions});
            }
            byte[] png=Png(width,height,rgba);var terrainBounds=new{minX=mincx*4096d,minY=mincy*4096d,maxX=(maxcx+1)*4096d,maxY=(maxcy+1)*4096d};
            return new(){["imageBase64"]=Convert.ToBase64String(png),["imageMimeType"]="image/png",["bounds"]=terrainBounds,["kind"]="installed-land-height-relief",["northUp"]=true,["widthPixels"]=width,["heightPixels"]=height,["pixelSizeGameUnits"]=4096d/samples,["sha256"]=Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant(),["cellCount"]=cells.Count,["excludedLandCount"]=excluded,["alphaNonzeroPixels"]=alpha,["alphaZeroPixels"]=(long)width*height-alpha,["pixelOrigin"]="northwest",["pixelCentreRule"]="x=minX+(column+0.5)*pixelSizeGameUnits; y=maxY-(row+0.5)*pixelSizeGameUnits",["verticesPerCellAxis"]=33,["heightSampleSpacingUnits"]=128,["vhgtScaleUnits"]=8,["source"]="Winning installed LAND/VHGT heights. Missing cells are transparent. Elevation tint and northwest hillshade.",["limits"]="Geographic relief only; roads, water, trails, obstacles and safe walking routes are not inferred.",["neighborSeamSamples"]=seamSamples,["neighborSeamNonzeroSamples"]=seamNonzero,["neighborSeamMaxAbsoluteUnits"]=seamMax,["coordinateConflicts"]=conflicts,["inheritance"]=inheritance,["landRecords"]=evidence};
        }
        static byte[] Png(int width,int height,byte[] rgba){
            using var result=new MemoryStream();result.Write(new byte[]{137,80,78,71,13,10,26,10});
            void Chunk(string tag,byte[] bytes){var length=new byte[4];BinaryPrimitives.WriteInt32BigEndian(length,bytes.Length);result.Write(length);var name=Encoding.ASCII.GetBytes(tag);result.Write(name);result.Write(bytes);uint crc=0xffffffff;foreach(byte v in name.Concat(bytes)){crc^=v;for(int i=0;i<8;i++)crc=(crc&1)!=0?(crc>>1)^0xedb88320:crc>>1;}BinaryPrimitives.WriteUInt32BigEndian(length,~crc);result.Write(length);}
            var ihdr=new byte[13];BinaryPrimitives.WriteInt32BigEndian(ihdr,width);BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4),height);ihdr[8]=8;ihdr[9]=6;Chunk("IHDR",ihdr);
            using var compressed=new MemoryStream();using(var z=new ZLibStream(compressed,CompressionLevel.Optimal,true))for(int y=0;y<height;y++){z.WriteByte(0);z.Write(rgba,y*width*4,width*4);}Chunk("IDAT",compressed.ToArray());Chunk("IEND",Array.Empty<byte>());return result.ToArray();
        }
    }
}
