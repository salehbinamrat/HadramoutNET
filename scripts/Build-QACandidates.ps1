[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('x86', 'x64')]
    [string]$Arch
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$rid = "win-$Arch"
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publish = Join-Path $root "out\publish\$rid"
$packages = Join-Path $root 'out\qa'
New-Item -ItemType Directory -Force -Path $publish, $packages | Out-Null

# This runner job is scheduled only AFTER successful Core and Release tests.
& dotnet publish (Join-Path $root 'src\HadramoutNET.App\HadramoutNET.App.csproj') --configuration Release --runtime $rid --self-contained true --output $publish -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=none -p:TreatWarningsAsErrors=true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }

$exe = Join-Path $publish 'HadramoutNET.App.exe'
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing $rid executable" }
if (!(Test-Path (Join-Path $publish 'HadramoutNET.Core.dll') -PathType Leaf)) { throw 'Missing core DLL' }

# Validate that the Portable executable itself is the requested PE architecture.
$stream = [System.IO.File]::OpenRead($exe)
try {
    $reader = [System.IO.BinaryReader]::new($stream)
    $stream.Position = 0x3c
    $offset = $reader.ReadInt32()
    if ($offset -lt 64 -or $offset -ge $stream.Length - 6) { throw 'Invalid PE header' }
    $stream.Position = $offset
    if ($reader.ReadUInt32() -ne 0x00004550) { throw 'Invalid PE signature' }
    $actual = $reader.ReadUInt16()
    $required = if ($Arch -eq 'x86') { 0x14c } else { 0x8664 }
    if ($actual -ne $required) { throw "Wrong PE architecture for $rid" }
}
finally { $stream.Dispose() }

$manifest = Join-Path $publish 'SHA256-FILES.txt'
$lines = [System.Collections.Generic.List[string]]::new()
Get-ChildItem -LiteralPath $publish -File -Recurse | Where-Object { $_.FullName -ne $manifest } | Sort-Object FullName | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    $relative = [System.IO.Path]::GetRelativePath($publish, $_.FullName)
    $lines.Add("$hash  $relative")
}
if ($lines.Count -lt 8) { throw 'Incomplete self-contained application output' }
[System.IO.File]::WriteAllLines($manifest, $lines)

$portable = Join-Path $packages "HadramoutNET-$rid-Portable-QA.zip"
if (Test-Path $portable) { Remove-Item -Force $portable }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $portable -CompressionLevel Optimal

$command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if ($null -ne $command) {
    $iscc = $command.Source
}
else {
    $iscc = @('C:\Program Files (x86)\Inno Setup 6\ISCC.exe', 'C:\Program Files\Inno Setup 6\ISCC.exe') | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($iscc)) { throw 'Inno Setup 6 compiler unavailable' }
& $iscc "/DArch=$Arch" "/DPublishDir=$publish" "/DOutputDir=$packages" (Join-Path $root 'installer\HadramoutNET-QA.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed for $rid" }

$installer = Join-Path $packages "HadramoutNET-$rid-Installer-QA.exe"
if (!(Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Installer not produced for $rid" }

$zip = [System.IO.Compression.ZipFile]::OpenRead($portable)
try {
    if (!($zip.Entries | Where-Object { $_.FullName -eq 'HadramoutNET.App.exe' })) {
        throw 'Portable archive missing executable'
    }
    if (!($zip.Entries | Where-Object { $_.FullName -eq 'SHA256-FILES.txt' })) {
        throw 'Portable archive missing file checksums'
    }
}
finally { $zip.Dispose() }

foreach ($path in @($portable, $installer)) {
    if ((Get-Item -LiteralPath $path).Length -lt 100000) {
        throw "Suspiciously small package: $path"
    }
}
$checksumFile = Join-Path $packages "HadramoutNET-$rid-SHA256.txt"
$checkLines = @($portable, $installer) | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash
    "$hash  $([System.IO.Path]::GetFileName($_))"
}
[System.IO.File]::WriteAllLines($checksumFile, $checkLines)
foreach ($line in $checkLines) {
    $expected, $name = $line -split '  ', 2
    $actual = (Get-FileHash -LiteralPath (Join-Path $packages $name) -Algorithm SHA256).Hash
    if ($expected -ne $actual) { throw "SHA256 mismatch: $name" }
}
Write-Host "Created two QA candidate artifacts for $rid."
Get-ChildItem $packages | Where-Object Name -Match $rid | Select-Object Name, Length | Format-Table -AutoSize
