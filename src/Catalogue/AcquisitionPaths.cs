using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace ScriptDesk.Catalogue;
internal sealed partial class CatalogueEngine {
    // Follow only field-verified supply edges. A generic FormLink can instead
    // mean a consumed ingredient, comparison operand, template or restriction.
    internal static object AcquisitionPaths(SqliteConnection db,string target){
        var paths=new List<object>();var unresolved=new List<object>();
        var queue=new Queue<(string key,List<object> steps,HashSet<string> ancestry)>();
        queue.Enqueue((target,new(),new(StringComparer.OrdinalIgnoreCase){target}));
        int inspected=0;bool truncated=false;
        while(queue.Count>0&&inspected<256&&paths.Count<96){
            var work=queue.Dequeue();
            var sources=Rows(db,"SELECT r.key,r.name,r.editor,r.type,r.winner,r.metadata FROM links l JOIN records r ON r.key=l.source WHERE l.target=$0 AND coalesce(r.deleted,0)=0 ORDER BY CASE r.type WHEN 'ConstructibleObject' THEN 0 WHEN 'Flora' THEN 1 WHEN 'Tree' THEN 1 WHEN 'Container' THEN 2 WHEN 'Npc' THEN 3 WHEN 'LeveledItem' THEN 4 WHEN 'Outfit' THEN 5 WHEN 'Book' THEN 6 ELSE 7 END,r.key LIMIT 65",work.key);
            if(sources.Count>64)truncated=true;
            foreach(var row in sources.Take(64)){
                if(inspected>=256||paths.Count>=96){truncated=true;break;}inspected++;
                string source=Convert.ToString(row["key"])!,type=Convert.ToString(row["type"])??"";
                if(work.ancestry.Contains(source))continue;
                using var json=JsonDocument.Parse(Convert.ToString(row["metadata"])??"{}");
                if(!json.RootElement.TryGetProperty("Fields",out var fields)||fields.ValueKind!=JsonValueKind.Object)continue;
                var match=SupplyEdge(type,fields,work.key);
                if(match==null){if(unresolved.Count<16)unresolved.Add(new{source,target=work.key,type,reason="Generic reference only; no supply field verified in indexed metadata. Truncated or older metadata may require Fresh record."});continue;}
                var (kind,field,path)=match.Value;
                var evidence=new Dictionary<string,object?>();
                foreach(string name in new[]{"Conditions","WorkbenchKeyword","WorkBenchKeyword","CreatedObjectCount","ChanceNone","Flags"})
                    if(fields.TryGetProperty(name,out var value))evidence[name]=BoundedEvidence(value);
                if(kind=="recipe-output"){
                    if(fields.TryGetProperty("Items",out var ingredients))evidence["Ingredients"]=BoundedEvidence(ingredients);
                    var station=RecipeStation(db,fields);
                    evidence["Station"]=station;
                    if(station is "CraftingSmithingSharpeningWheel" or "CraftingSmithingArmorTable")kind="recipe-upgrade";
                }
                string nameText=Convert.ToString(row["name"])??"";if(nameText.Length==0)nameText=Convert.ToString(row["editor"])??source;
                var step=new{source,target=work.key,name=nameText,type,plugin=row["winner"],kind,field,metadataPath=path,evidence};
                var steps=new List<object>(work.steps){step};
                string action=kind switch {
                    "recipe-output"=>"Recipe references this output; it may create or upgrade an existing item. Check the station, ingredients and conditions with Fresh record before treating it as an acquisition route.",
                    "recipe-upgrade"=>"This tempering recipe upgrades an existing item; it does not provide a new item.",
                    "harvest-produce"=>"Locate an instance of this plant or tree and check normal harvest eligibility.",
                    "container-content"=>"Locate this container base in the world and verify access and current contents.",
                    "npc-inventory"=>"Inspect this NPC's actual inventory and the environment's normal acquisition rules.",
                    "npc-outfit"=>"Inspect this NPC's equipped outfit; equipment is not proof of a loot or purchase route.",
                    "book-teaches"=>"Check the environment's learning rules; this is only the recorded vanilla teaching relationship.",
                    _=>"Follow this distribution record to a container or NPC; a list alone is not a world location."
                };
                paths.Add(new{kind,source,name=nameText,action,steps,conditionsEvaluated=false,liveAvailability=(bool?)null});
                if(type is "LeveledItem" or "Outfit"){
                    if(steps.Count>=3){truncated=true;continue;}
                    var ancestry=new HashSet<string>(work.ancestry,StringComparer.OrdinalIgnoreCase){source};queue.Enqueue((source,steps,ancestry));
                }
            }
        }
        if(queue.Count>0)truncated=true;
        return new{paths,unresolved,inspected,truncated,maxDepth=3,maxRecords=256,maxPaths=96,
            note="Acquisition hypotheses from indexed winning records. Recipe inputs are not outputs. Conditions, leveled-list selection, world placement, ownership and server eligibility still require verification; missing paths do not establish inaccessibility."};
    }
    static object BoundedEvidence(JsonElement value)=>value.GetRawText().Length<=4096?value.Clone():new{truncated=true,reason="Field exceeds the acquisition summary limit; inspect Fresh record for full projected metadata."};
    static string? RecipeStation(SqliteConnection db,JsonElement fields){
        foreach(string name in new[]{"WorkbenchKeyword","WorkBenchKeyword"})
            if(fields.TryGetProperty(name,out var station)&&station.ValueKind==JsonValueKind.Object&&station.TryGetProperty("FormKey",out var key)&&key.ValueKind==JsonValueKind.String){
                var row=Rows(db,"SELECT editor FROM records WHERE key=$0 AND type='Keyword' AND coalesce(deleted,0)=0",key.GetString()).FirstOrDefault();
                if(row!=null)return Convert.ToString(row["editor"]);
            }
        return null;
    }
    internal static (string kind,string field,string path)? SupplyEdge(string type,JsonElement fields,string target){
        (string field,string kind)[] candidates=type switch {
            "ConstructibleObject"=>new[]{("CreatedObject","recipe-output"),("ResultingItem","recipe-output")},
            "Flora" or "Tree"=>new[]{("Ingredient","harvest-produce")},
            "Container"=>new[]{("Items","container-content")},
            "Npc"=>new[]{("Items","npc-inventory"),("DefaultOutfit","npc-outfit"),("SleepingOutfit","npc-outfit")},
            "LeveledItem"=>new[]{("Entries","leveled-entry")},
            "Outfit"=>new[]{("Items","outfit-item")},
            "Book"=>new[]{("Teaches","book-teaches")},
            _=>Array.Empty<(string,string)>()
        };
        foreach(var candidate in candidates)if(fields.TryGetProperty(candidate.field,out var value)){
            int budget=2048;var path=FindSupplyLink(value,target,"Fields."+candidate.field,0,ref budget);
            if(path!=null)return(candidate.kind,candidate.field,path);
        }
        return null;
    }
    static string? FindSupplyLink(JsonElement value,string target,string path,int depth,ref int budget){
        if(depth>10||--budget<0)return null;
        if(value.ValueKind==JsonValueKind.String)return string.Equals(value.GetString(),target,StringComparison.OrdinalIgnoreCase)?path:null;
        if(value.ValueKind==JsonValueKind.Array){int n=0;foreach(var item in value.EnumerateArray()){if(n>=64)break;var found=FindSupplyLink(item,target,path+"["+n+++"]",depth+1,ref budget);if(found!=null)return found;}}
        if(value.ValueKind==JsonValueKind.Object)foreach(var property in value.EnumerateObject()){
            // Conditions and scripts inside entries must not turn requirements
            // into supply relationships. Follow only known inventory/link shapes.
            if(property.Name is not ("FormKey" or "Item" or "Reference" or "Data" or "Spell" or "Link"))continue;
            var found=FindSupplyLink(property.Value,target,path+"."+property.Name,depth+1,ref budget);if(found!=null)return found;
        }
        return null;
    }
}
