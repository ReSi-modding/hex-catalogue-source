namespace ScriptDesk.Catalogue;
internal static class TexturePreview {
    internal static byte[] Decode(byte[] bytes){return DecodeCore(bytes,1024,1024,8*1024*1024,true,null,false);}
    internal static byte[] DecodeModel(byte[] bytes,NifMaterial? material=null,bool glow=false){return DecodeCore(bytes,4096,256,16*1024*1024,false,material,glow);}
    internal static byte[] DecodeSpecularMask(byte[] bytes){return DecodeCore(bytes,4096,256,16*1024*1024,true,null,false,true);}
    static byte[] DecodeCore(byte[] bytes,int dimensionLimit,int outputLimit,int byteLimit,bool preserveAlpha,NifMaterial? material,bool glow,bool specularMask=false){
        if(bytes.Length<128||bytes.Length>byteLimit||BitConverter.ToUInt32(bytes,0)!=0x20534444)throw new InvalidDataException("Invalid or oversized DDS.");
        uint h=BitConverter.ToUInt32(bytes,12),w=BitConverter.ToUInt32(bytes,16);if(w<1||h<1||w>dimensionLimit||h>dimensionLimit)throw new InvalidDataException("Preview dimensions exceed the bounded decoder limit.");
        using var stream=new MemoryStream(bytes,false);using var image=Pfim.Pfimage.FromStream(stream);
        if(image.Width!=(int)w||image.Height!=(int)h||image.Compressed)throw new InvalidDataException("DDS dimensions or compression could not be decoded.");
        int bpp=image.BitsPerPixel;if(bpp!=24&&bpp!=32)throw new InvalidDataException("Preview currently supports RGB24 and RGBA32 DDS images.");
        double scale=Math.Min(1,(double)outputLimit/Math.Max(image.Width,image.Height));int width=Math.Max(1,(int)(image.Width*scale)),height=Math.Max(1,(int)(image.Height*scale));
        if(image.Stride<image.Width*(bpp/8)||image.Data.Length<(image.Height-1)*image.Stride+image.Width*(bpp/8))throw new InvalidDataException("Incomplete decoded DDS pixels.");
        // PNG is decoded consistently by both the Windows texture view and WPF.
        // The former 24-bit BMP conversion discarded every source alpha value.
        var pixels=new byte[(width*4+1)*height];
        for(int y=0;y<height;y++){int row=(y*image.Height/height)*image.Stride,target=y*(width*4+1);pixels[target++]=0;
            for(int x=0;x<width;x++){int i=row+(x*image.Width/width)*(bpp/8);for(int channel=0;channel<3;channel++){float factor=glow&&material!=null?material.emissiveColor[channel]*material.emissiveMultiple:1;pixels[target++]=specularMask?(byte)255:(byte)Math.Clamp(Math.Round(image.Data[i+2-channel]*factor),0,255);}byte alpha=bpp==32?image.Data[i+3]:(byte)255;pixels[target++]=preserveAlpha?alpha:material?.Alpha(alpha)??(byte)255;}}
        using var output=new MemoryStream();output.Write(new byte[]{137,80,78,71,13,10,26,10});
        var header=new byte[13];System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0,4),width);System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4,4),height);header[8]=8;header[9]=6;Chunk(output,"IHDR",header);
        using var compressed=new MemoryStream();using(var zip=new System.IO.Compression.ZLibStream(compressed,System.IO.Compression.CompressionLevel.Fastest,true))zip.Write(pixels);
        Chunk(output,"IDAT",compressed.ToArray());Chunk(output,"IEND",Array.Empty<byte>());return output.ToArray();
    }
    static void Chunk(Stream stream,string type,byte[] data){
        var tag=System.Text.Encoding.ASCII.GetBytes(type);Span<byte> number=stackalloc byte[4];System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(number,data.Length);stream.Write(number);stream.Write(tag);stream.Write(data);
        uint crc=0xffffffff;foreach(byte b in tag.Concat(data)){crc^=b;for(int bit=0;bit<8;bit++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0u);}
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number,~crc);stream.Write(number);
    }
}
