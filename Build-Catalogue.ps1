param([Parameter(Mandatory=$true)][string]$Dotnet,[Parameter(Mandatory=$true)][string]$SourceRoot,[Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputRoot){throw 'Choose a new output root; never build over running output.'}
$config=Get-Content -Raw -LiteralPath (Join-Path $SourceRoot 'sdk.json') | ConvertFrom-Json
if((& $Dotnet --version) -ne $config.sdk){throw 'Use the reviewed SDK version.'}
$project=Join-Path $SourceRoot 'src\Catalogue\ScriptDesk.Indexer.csproj'
$feed=Join-Path $SourceRoot 'vendor\packages'
# Keep the fresh NuGet cache short even if the source checkout path is long;
# Windows MSBuild file-copy tasks can still hit MAX_PATH for long package names.
$packages=Join-Path ([IO.Path]::GetTempPath()) ('hex-src-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
$build=Join-Path $OutputRoot 'build'
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
& $Dotnet restore $project --locked-mode --source $feed --packages $packages -r win-x64 -p:SelfContained=false -p:EnableRuntimePackDownload=false -p:HexCatalogueFramework=net10.0 -p:NuGetAudit=false
if($LASTEXITCODE){throw 'Offline locked restore failed.'}
& $Dotnet build $project --no-restore -c Release -o $build -r win-x64 --self-contained false -p:EnableRuntimePackDownload=false -p:HexCatalogueFramework=net10.0 -p:HexTestBuild=false -p:NuGetAudit=false
if($LASTEXITCODE){throw 'Independent helper source build failed.'}
Write-Output 'Production helper built independently from the source review and locked offline package feed; no game or frontend launched.'
