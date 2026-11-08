using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using System.Text.Json;

namespace ScriptDesk.Catalogue;
internal sealed partial class CatalogueEngine {
    // Narrow disk research: one known cell and one reference. Placed references
    // are deliberately not added to the full catalogue/indexing record types.
    internal static IPlacedObjectGetter? SelectedPlacement(ICellGetter cell,FormKey reference) {
        var matches=cell.Persistent.Concat(cell.Temporary).Where(x=>x.FormKey==reference).Take(2).ToArray();
        if(matches.Length>1)throw new InvalidDataException("Duplicate selected reference in cell groups.");
        if(matches.Length==1&&matches[0] is not IPlacedObjectGetter)throw new InvalidDataException("Selected reference is not a placed object.");
        return matches.FirstOrDefault() as IPlacedObjectGetter;
    }
    object PlacedReference(JsonElement request) {
        RequireIndex();string cellText=Text(request,"cellKey"),referenceText=Text(request,"key");
        if(cellText.Length>260||referenceText.Length>260)throw new InvalidDataException("Invalid selected identity.");
        var cellKey=FormKey.Factory(cellText);var reference=FormKey.Factory(referenceText);
        if(cellKey.IsNull||reference.IsNull)throw new InvalidDataException("A stable cell and reference FormKey are required.");
        using var db=Open(database,true);
        var rows=Rows(db,"SELECT v.plugin,v.ordinal FROM versions v JOIN records r ON r.key=v.key WHERE v.key=$0 AND r.type='Cell' ORDER BY v.ordinal",cellKey.ToString());
        if(rows.Count==0||rows.Count>256)throw new InvalidDataException("No bounded indexed cell history. Build a current catalogue first.");
        string listingHash=HashFile(pluginsPath),cccHash=HashFile(cccPath);
        if(Convert.ToString(Scalar(db,"SELECT value FROM meta WHERE key='pluginsHash'"))!=listingHash||Convert.ToString(Scalar(db,"SELECT value FROM meta WHERE key='cccHash'"))!=cccHash)
            throw new InvalidDataException("Load order changed since indexing. Rebuild the catalogue.");
        var active=LoadOrder.GetLoadOrderListings(GameRelease.SkyrimSE,pluginsPath,File.Exists(cccPath)?cccPath:null,dataPath).Where(x=>x.Enabled).Select(x=>x.ModKey).ToHashSet();
        var versions=new List<object>();RecordData? selected=null;string? lastPlugin=null;
        var checkedPlugins=new List<string>();
        foreach(var row in rows) {
            string plugin=(string)row["plugin"]!;var modKey=ModKey.FromNameAndExtension(plugin);
            if(Path.GetFileName(plugin)!=plugin||!active.Contains(modKey))throw new InvalidDataException("Indexed cell plugin is no longer active.");
            var fingerprint=Rows(db,"SELECT size,modified FROM plugins WHERE name=$0",plugin).Single();
            var info=new FileInfo(Path.Combine(dataPath,plugin));info.Refresh();
            if(!info.Exists||info.Length!=Convert.ToInt64(fingerprint["size"])||info.LastWriteTimeUtc.ToString("O")!=Convert.ToString(fingerprint["modified"]))
                throw new InvalidDataException("Cell source changed since indexing: "+plugin);
            long size=info.Length;var modified=info.LastWriteTimeUtc;
            using var mod=SkyrimMod.CreateFromBinaryOverlay(new ModPath(modKey,info.FullName),SkyrimRelease.SkyrimSE);
            var cell=mod.EnumerateMajorRecords<ICellGetter>().FirstOrDefault(x=>x.FormKey==cellKey)??throw new InvalidDataException("Indexed cell disappeared.");
            var placement=SelectedPlacement(cell,reference);checkedPlugins.Add(plugin);
            if(placement!=null){selected=RecordData.Read(placement);lastPlugin=plugin;versions.Add(new{plugin,record=selected});}
            info.Refresh();if(info.Length!=size||info.LastWriteTimeUtc!=modified)throw new IOException("Plugin changed during selected placement read.");
        }
        if(HashFile(pluginsPath)!=listingHash||HashFile(cccPath)!=cccHash)throw new IOException("Load order changed during selected placement read.");
        return new{key=reference.ToString(),cellKey=cellKey.ToString(),found=selected!=null,record=selected,lastInspectedOverride=lastPlugin,versions,checkedPlugins,readOnly=true,
            scope="Indexed versions of this exact cell only. Not a global reference search or runtime eligibility decision.",
            indexWarnings=JsonSerializer.Deserialize<JsonElement>(Convert.ToString(Scalar(db,"SELECT value FROM meta WHERE key='warnings'"))??"[]"),
            note="Placed links, ownership and lock data are static research leads. Current instance state, server rewards, moved references and unindexed records require separate runtime verification."};
    }
}
