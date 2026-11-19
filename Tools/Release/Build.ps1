# Makes a release's downloads for one system, in dist/:
#   Vertex-<version>-<runtime>.zip (Windows) or .tar.gz (Linux, macOS)
#                                   the editor, the engine's package, the samples and a starter game, ready to use
#   Vertex.Engine.<version>.nupkg   the engine's package on its own (the same on every system)
# The release workflow (.github/workflows/release.yml) runs it on each system; it runs on a PC too:
#   pwsh -File Tools/Release/Build.ps1 -Version 0.1.0 -Runtime win-x64     (or linux-x64, osx-arm64)
# Build on the system the download is for: on a Mac, .NET signs the editor so macOS will run it.

param(
    [string]$Version = "0.0.0-dev",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$dist = Join-Path $root "dist"
$name = "Vertex-$Version"
$folder = Join-Path $dist $name

function Invoke-Dotnet
{
    # Its errors are repeated as annotations when run on GitHub, so they show on the run's page.
    & dotnet @args | ForEach-Object {
        $line = "$_"
        Write-Host $line
        if ($env:GITHUB_ACTIONS -and $line -match '\berror\b') { Write-Host "::error::$line" }
    }
    if ($LASTEXITCODE -ne 0) { throw "dotnet $args failed" }
}

# A folder's files, without what building leaves in it (bin, obj).
function Copy-Source([string]$from, [string]$to)
{
    $base = (Resolve-Path $from).Path
    foreach ($file in Get-ChildItem $base -Recurse -File)
    {
        $relative = $file.FullName.Substring($base.Length + 1)
        if ($relative -match '(^|[\\/])(bin|obj)[\\/]') { continue }
        $target = Join-Path $to $relative
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Copy-Item $file.FullName $target
    }
}

$windows = $Runtime.StartsWith("win")
$mac = $Runtime.StartsWith("osx")

# What the README says to run, and anything to know on this system.
$editor = if ($windows) { "Editor\Vertex.Editor.exe" } else { "./Editor/Vertex.Editor (from a terminal in this folder)" }
$notes = if ($mac)
{
    "  - macOS won't open programs from the internet that aren't signed with an Apple developer account. Once, before`n" +
    "    the first run, open a terminal in this folder and run:  xattr -dr com.apple.quarantine .`n"
}
elseif (-not $windows)
{
    "  - Vulkan or OpenGL drivers from your distribution (Mesa, or the GPU maker's).`n"
}
else { "" }

# A text file from Tools/Release with the version (and the system's details) filled in.
function Write-WithVersion([string]$from, [string]$to)
{
    $text = [IO.File]::ReadAllText($from).Replace('$(VertexVersion)', $Version).Replace('$(VertexEditor)', $editor)
    $text = [regex]::Replace($text, '\$\(VertexPlatformNotes\)\r?\n', $notes.Replace('$', '$$'))
    [IO.File]::WriteAllText($to, $text)
}

if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force $folder | Out-Null

# The editor. Not self-contained: it builds games with the .NET SDK, which brings the runtime anyway, and compiles them
# against the engine files beside it, which mustn't include a copy of the framework's.
Invoke-Dotnet publish (Join-Path $root "Vertex.Editor/Vertex.Editor.csproj") -c Release -r $Runtime --self-contained false `
    "-p:Version=$Version" -o (Join-Path $folder "Editor") -nologo

# The engine's package.
Invoke-Dotnet pack (Join-Path $root "Vertex/Vertex.csproj") -c Release "-p:Version=$Version" -o (Join-Path $folder "Engine") -nologo

# The samples and the starter, using the engine's package instead of the engine's project.
foreach ($sample in "CodeOnly", "Duel", "NeonCity")
{
    $to = Join-Path $folder "Samples/$sample"
    Copy-Source (Join-Path $root "Samples/$sample") $to
    $project = Get-ChildItem $to -Filter *.csproj | Select-Object -First 1
    $text = [IO.File]::ReadAllText($project.FullName)
    $text = [regex]::Replace($text, '<ProjectReference Include="[^"]*Vertex\.csproj"\s*/>', "<PackageReference Include=`"Vertex.Engine`" Version=`"$Version`" />")
    [IO.File]::WriteAllText($project.FullName, $text)
}

$starter = Join-Path $folder "Starter"
New-Item -ItemType Directory -Force $starter | Out-Null
Copy-Item (Join-Path $PSScriptRoot "Starter/Program.cs") $starter
Write-WithVersion (Join-Path $PSScriptRoot "Starter/MyGame.csproj") (Join-Path $starter "MyGame.csproj")
Write-WithVersion (Join-Path $PSScriptRoot "README.txt") (Join-Path $folder "README.txt")
Copy-Item (Join-Path $PSScriptRoot "nuget.release.config") (Join-Path $folder "nuget.config")
Copy-Item (Join-Path $root "LICENSE") (Join-Path $folder "LICENSE.txt")

# Packed, with the version's folder inside so it unpacks tidily: a zip for Windows, and for Linux and macOS a .tar.gz,
# which keeps the editor marked as a program that can be run.
if ($windows)
{
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Join-Path $dist "$name-$Runtime.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($folder, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
}
else
{
    & tar -czf (Join-Path $dist "$name-$Runtime.tar.gz") -C $dist $name
    if ($LASTEXITCODE -ne 0) { throw "tar failed" }
}
Copy-Item (Join-Path $folder "Engine/Vertex.Engine.$Version.nupkg") $dist

Write-Host "Made:"
Get-ChildItem $dist -File | ForEach-Object { Write-Host ("  {0} ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)) }
