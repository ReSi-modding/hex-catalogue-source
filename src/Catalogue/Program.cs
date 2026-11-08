using System.Text.Json;
using ScriptDesk.Catalogue;

// One bounded local stdio request. No game connection, network listener, or SQL input.
try {
#if HEX_TEST_BUILD
    if(args.Contains("--acquisition-test")){AcquisitionChecks.Run();return;}
    if(args.Contains("--self-test")){CatalogueChecks.Run();return;}
#else
    if(args.Length>0)throw new InvalidDataException("Commercial catalogue requires a signed request; diagnostic fixture modes are not available.");
#endif
    string? cacheRoot=null;if(args.Contains("--fixture")){cacheRoot=Environment.GetEnvironmentVariable("SCRIPT_DESK_INDEXER_FIXTURE_ROOT");if(string.IsNullOrEmpty(cacheRoot)||!Path.IsPathFullyQualified(cacheRoot)||cacheRoot.StartsWith(@"\\"))throw new InvalidDataException("An absolute local fixture cache root is required.");cacheRoot=Path.GetFullPath(cacheRoot);}
    var input=new System.Text.StringBuilder();int c;
    while((c=Console.Read())!=-1&&c!='\n'){if(input.Length>=393216)throw new InvalidDataException("Request exceeds 384 KiB including authorization.");input.Append((char)c);}
    using var json=JsonDocument.Parse(input.ToString(),new JsonDocumentOptions{MaxDepth=16});
    CatalogueAuthorization.Require(json.RootElement);
    var result=new CatalogueEngine(json.RootElement,cacheRoot).Execute(json.RootElement);
    CatalogueAuthorization.Require(json.RootElement);
    Console.WriteLine(JsonSerializer.Serialize(new{ok=true,data=result}));
}catch(Exception e){
    Console.WriteLine(JsonSerializer.Serialize(new{ok=false,error=e.GetBaseException().Message}));Environment.ExitCode=1;
}
