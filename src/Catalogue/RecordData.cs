using System.Collections;
using System.Reflection;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins;

namespace ScriptDesk.Catalogue;
internal sealed class RecordData {
    public string Key{get;set;}="";public string Origin{get;set;}="";public uint LocalId{get;set;}
    public string? Name{get;set;}public string? EditorId{get;set;}public string Type{get;set;}="";public string Category{get;set;}="";
    public bool Deleted{get;set;}public bool? Playable{get;set;}public bool Enchanted{get;set;}
    public double? Weight{get;set;}public double? Value{get;set;}public double? Damage{get;set;}public double? Armor{get;set;}
    public List<string> Keywords{get;set;}=new();public List<string> Links{get;set;}=new();public List<string> Assets{get;set;}=new();
    public Dictionary<string,object?> Fields{get;set;}=new();public List<string> Unavailable{get;set;}=new();
    static readonly Dictionary<string,string> Categories=new(){
        ["Weapon"]="Weapons",["Armor"]="Armor",["Ammunition"]="Ammunition",["Book"]="Books / Tomes",["Ingestible"]="Potions / Food",["Ingredient"]="Ingredients",["MiscItem"]="Miscellaneous",["Container"]="Containers",["Key"]="Keys",["Scroll"]="Scrolls",["SoulGem"]="Soul Gems",["Spell"]="Spells",["MagicEffect"]="Magic Effects",["ObjectEffect"]="Enchantments",["Perk"]="Perks",["Npc"]="NPCs",["Race"]="Races",["Quest"]="Quests",["Static"]="World Objects",["MoveableStatic"]="World Objects",["Activator"]="World Objects",["Door"]="World Objects",["Furniture"]="World Objects",["Flora"]="Harvestables",["Tree"]="Harvestables",["LeveledItem"]="Leveled Lists",["LeveledNpc"]="Leveled Lists",["ConstructibleObject"]="Recipes",["FormList"]="Form Lists",["Global"]="Globals",["GameSetting"]="Game Settings",["Keyword"]="Keywords",["Outfit"]="Outfits",["TextureSet"]="Textures",["ArmorAddon"]="Armor Models",["Projectile"]="Projectiles",["Location"]="Locations",["Worldspace"]="Worldspaces",["Cell"]="Cells"};
    internal static IEnumerable<Type> RecordTypes=>Categories.Keys.Select(n=>typeof(Mutagen.Bethesda.Skyrim.SkyrimMod).Assembly.GetType("Mutagen.Bethesda.Skyrim.I"+n+"Getter")).Where(t=>t!=null).Cast<Type>();
    static readonly string[] Selected={"Diffuse","NormalOrGloss","EnvironmentMaskOrSubsurfaceTint","GlowOrDetailMap","Height","Environment","Multilayer","BacklightMaskOrSpecular","VirtualMachineAdapter","Name","Description","Weight","Value","Damage","ArmorRating","Flags","MajorRecordFlagsRaw","Model","WorldModel","FirstPersonModel","MaleWorldModel","FemaleWorldModel","Icons","Icon","SmallIcon","BodyTemplate","EquipmentType","ObjectEffect","EnchantmentAmount","Data","BasicStats","AttackData","Critical","Teaches","Effects","Keywords","Race","Class","Configuration","PlayerSkills","Items","Entries","Conditions","Stages","Aliases","ValueType","HarvestSound","Ingredient","ResultingItem","CreatedObject","CreatedObjectCount","WorkBenchKeyword","Quest","SpellList","Spells","Perks","HeadParts","BodyData","MaleBehaviorGraph","FemaleBehaviorGraph"};
    static readonly HashSet<string> Nested=new(new[]{"Scripts","Properties","Object","String","Bool","Int","Float","Name","File","RawPath","DataRelativePath","Male","Female","Model","Icon","SmallIcon","InventoryIcon","MessageIcon","Weight","Value","Damage","Speed","Reach","Stagger","RangeMin","RangeMax","AnimationType","Flags","Type","CastType","Delivery","TargetType","Cost","BaseCost","ChargeTime","CastDuration","HalfCostPerk","BaseEffect","ObjectEffect","Data","Magnitude","Area","Duration","Skill","Spell","Level","Count","Reference","Item","FormKey","Health","Magicka","Stamina","Priority","Index","Stage","EntryPoint","ArmorRating","SkillBoosts","SkillValues","ValueType","Value","Amount","ChanceNone","ItemCount","Conditions"});
    static readonly Dictionary<(Type,string),PropertyInfo?> Properties=new();
    // Model texture overrides are nested under the chosen model, not the armor's
    // separate skin-texture FormLists. Keep their exact shape index and TXST link.
    
    static RecordData(){Nested.UnionWith(new[]{"AlternateTextures","NewTexture","FirstPersonFlags","ArmorType","AdditionalRaces","ActorValue","Association","Position","Rotation","X","Y","Z","Key","Keyword","Parent","Reference","Delay","Function","ComparisonValue","CompareOperator","RunOnType","RunOnReference","ParameterOne","ParameterTwo","Perk","ItemOrList","Link","UseAliases","UsePackageData","RunOnTypeIndex","ForcedReference","UniqueActor","SpecificLocation","CreateReferenceToObject","Object","AliasIDToForceIntoWhenFilled"});}
    internal static object? Get(object? o,string name){if(o==null)return null;var key=(o.GetType(),name);if(!Properties.TryGetValue(key,out var p)){p=key.Item1.GetProperty(name)??key.Item1.GetInterfaces().Select(t=>t.GetProperty(name)).FirstOrDefault(x=>x!=null);Properties[key]=p;}return p?.GetIndexParameters().Length==0?p.GetValue(o):null;}
    static string? Str(object? x)=>x?.ToString();
    static double? Number(object? x){if(x==null)return null;try{double v=Convert.ToDouble(x,System.Globalization.CultureInfo.InvariantCulture);return double.IsFinite(v)?v:null;}catch{return null;}}
    internal static RecordData? Read(IMajorRecordGetter record){
        string type=record.GetType().Name.Replace("BinaryOverlay","");if(type.StartsWith("Global"))type="Global";if(type.StartsWith("GameSetting"))type="GameSetting";string? category;if(type=="PlacedObject")category="Placed References";else if(!Categories.TryGetValue(type,out category))return null;
        var m=new RecordData{Key=record.FormKey.ToString(),Origin=record.FormKey.ModKey.ToString(),LocalId=record.FormKey.ID,Type=type,Category=category,EditorId=record.EditorID,Deleted=record.IsDeleted};
        // These records store important physics/effect fields directly, not under
        // Data. Retain them for selected source research without editing records.
        var researchFields=type=="Projectile"?new[]{"Type","Speed","Gravity","Range","ConeSpread","CollisionRadius","Lifetime","RelaunchInterval","ImpactForce","Explosion","DefaultWeaponSource"}:type=="MagicEffect"?new[]{"Archetype","BaseCost","MagicSkill","ResistValue","SpellmakingCastingTime","TaperWeight","TaperCurve","TaperDuration","Projectile","CastType","TargetType","SecondActorValue","SecondActorValueWeight","PerkToApply","Conditions"}:type=="Spell"?new[]{"Type","CastType","TargetType","ChargeTime","CastDuration","HalfCostPerk"}:type=="PlacedObject"?new[]{"Base","Placement","LocationRefTypes","PersistentLocation","LocationReference","Owner","FactionRank","Lock","EnableParent","LinkedReferences","ActivateParents","EncounterZone","IsOpenByDefault","Scale","Charge"}:new string[0];
        var armorFields=type=="ArmorAddon"?new[]{"Priority","WeightSliderEnabled","SkinTexture","TextureSwapList","WeaponAdjust","FootstepSound","ArtObject"}:type=="Armor"?new[]{"TemplateArmor"}:Array.Empty<string>();
        foreach(string field in Selected.Concat(new[]{"Armature","AdditionalRaces","WorkbenchKeyword"}).Concat(researchFields).Concat(armorFields).Distinct())try{var v=Get(record,field);if(v!=null)m.Fields[field]=Project(v,0);}catch(Exception e){m.Unavailable.Add(field+": "+e.GetBaseException().Message);}
        m.Name=Str(Get(record,"Name"));m.Weight=Number(Get(record,"Weight"))??Number(Get(Get(record,"BasicStats"),"Weight"));m.Value=Number(Get(record,"Value"))??Number(Get(Get(record,"BasicStats"),"Value"));m.Damage=Number(Get(record,"Damage"))??Number(Get(Get(record,"BasicStats"),"Damage"));m.Armor=Number(Get(record,"ArmorRating"));
        var flagsValue=Get(record,"MajorFlags")??Get(record,"Flags")??Get(Get(record,"Data"),"Flags");string flags=Str(flagsValue)??"";
        // Only classify when the parser exposes an enum that actually declares NonPlayable.
        if(flagsValue!=null&&flagsValue.GetType().IsEnum&&Enum.GetNames(flagsValue.GetType()).Any(n=>n.Contains("NonPlayable",StringComparison.OrdinalIgnoreCase)))m.Playable=!flags.Contains("NonPlayable",StringComparison.OrdinalIgnoreCase);
        var enchant=Get(Get(record,"ObjectEffect"),"FormKey");m.Enchanted=enchant is FormKey fk&&!fk.IsNull;
        if(Get(record,"Keywords") is IEnumerable keywords)foreach(var keyword in keywords){var id=Get(keyword,"FormKey");if(id!=null)m.Keywords.Add(id.ToString()!);}
        try{foreach(var link in record.EnumerateFormLinks()){var fk2=link.FormKey;if(!fk2.IsNull&&m.Links.Count<4096)m.Links.Add(fk2.ToString());}}catch(Exception e){m.Unavailable.Add("Links: "+e.GetBaseException().Message);}
        CollectAssets(m.Fields,m.Assets);m.Assets=m.Assets.Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToList();return m;
    }
    internal static object? Project(object? v,int depth=0){int budget=2048;return ProjectBounded(v,depth,ref budget);}
    static object? ProjectBounded(object? v,int depth,ref int budget){
        if(--budget<0)return "[metadata node limit]";
        if(v==null)return null;var t=v.GetType();if(v is string s)return s.Length>1024?s[..1024]+" [truncated]":s;if(t.IsPrimitive||v is decimal)return v;if(t.IsEnum||v is FormKey||v is ModKey)return v.ToString();if(t.Name.Contains("TranslatedString"))return v.ToString();if(depth>=8)return "[metadata depth limit: "+t.Name+"]";
        // Mutagen gendered model/texture/priority pairs are also enumerable.
        // Keep their labels before the generic sequence path loses them.
        if(v is IEnumerable&&t.GetInterfaces().Append(t).Any(i=>i.GetProperty("Male")!=null&&i.GetProperty("Female")!=null))
            return new Dictionary<string,object?>{{"Male",ProjectBounded(Get(v,"Male"),depth+1,ref budget)},{"Female",ProjectBounded(Get(v,"Female"),depth+1,ref budget)}};
        if(v is IEnumerable<byte>)return "Binary metadata (not copied to catalogue)";
        if(v is IEnumerable seq){var list=new List<object?>();foreach(var x in seq){if(list.Count>=64||budget<=0){list.Add(budget<=0?"[metadata node limit]":"[limited to 64]");break;}list.Add(ProjectBounded(x,depth+1,ref budget));}return list;}
        var dict=new Dictionary<string,object?>();foreach(string name in Nested){if(budget<=0){dict["Unavailable"]="Metadata node limit";break;}try{var x=Get(v,name);if(x!=null)dict[name]=ProjectBounded(x,depth+1,ref budget);}catch(Exception e){dict[name]="[unavailable: "+e.GetBaseException().Message+"]";}}return dict.Count>0?dict:v.ToString();
    }
    static void CollectAssets(object? v,List<string> output){if(v is string s&&(s.EndsWith(".nif",StringComparison.OrdinalIgnoreCase)||s.EndsWith(".dds",StringComparison.OrdinalIgnoreCase)))output.Add(s);else if(v is Dictionary<string,object?> d)foreach(var item in d.Values)CollectAssets(item,output);else if(v is IEnumerable seq&&v is not string)foreach(var item in seq)CollectAssets(item,output);}
}

