using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace ScriptDesk.Catalogue;
internal sealed partial class CatalogueEngine {
    internal static string? TeachingSpell(string metadata){
        using var json=JsonDocument.Parse(metadata);var root=json.RootElement;
        if(!root.TryGetProperty("Fields",out var fields)||fields.ValueKind!=JsonValueKind.Object||!fields.TryGetProperty("Teaches",out var teaches)||teaches.ValueKind!=JsonValueKind.Object||!teaches.TryGetProperty("Spell",out var spell)||spell.ValueKind!=JsonValueKind.Object||!spell.TryGetProperty("FormKey",out var key)||key.ValueKind!=JsonValueKind.String)return null;
        string? value=key.GetString();return value!=null&&value!="Null"?value:null;
    }
    object TomeAssociations(SqliteConnection db,string key,string type){
        var rows=Rows(db,"SELECT v.key,v.plugin,v.metadata,r.name,r.winner,r.metadata AS winning FROM versions v JOIN records r ON r.key=v.key WHERE r.type='Book' AND "+(type=="Book"?"v.key=$0":"json_extract(v.metadata,'$.Fields.Teaches.Spell.FormKey')=$0")+" LIMIT 257",key);
        var entries=new List<object>();
        foreach(var group in rows.Take(256).GroupBy(r=>Convert.ToString(r["key"])!)){
            var current=group.First();string? currentSpell=TeachingSpell((string)current["winning"]!);
            foreach(var relation in group.Select(r=>new{spell=TeachingSpell((string)r["metadata"]!),plugin=(string)r["plugin"]!}).Where(r=>r.spell!=null).GroupBy(r=>r.spell!)){
                if(type!="Book"&&!String.Equals(relation.Key,key,StringComparison.OrdinalIgnoreCase))continue;
                entries.Add(new{tomeKey=group.Key,spellKey=relation.Key,name=current["name"],winningPlugin=current["winner"],currentTeachingSpell=currentSpell,currentAssociation=String.Equals(currentSpell,relation.Key,StringComparison.OrdinalIgnoreCase),associationPlugins=relation.Select(r=>r.plugin).Distinct().ToArray(),environmentLearningAvailable=(bool?)null});
            }
        }
        return new{rows=entries,truncated=rows.Count>256,note="Exact teaching links in indexed record versions. A historical association removed by the winning plugin does not establish current tome learning or ownership."};
    }
}
