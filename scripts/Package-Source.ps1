param([Parameter(Mandatory)][string]$DestinationPath)
$ErrorActionPreference = 'Stop'
$sourceRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (![System.IO.Path]::IsPathFullyQualified($DestinationPath)) { throw 'Destination must be an absolute new ZIP path' }
$destination = [System.IO.Path]::GetFullPath($DestinationPath)
if (Test-Path -LiteralPath $destination) { throw 'Destination already exists' }
if ([System.IO.Path]::GetExtension($destination) -ne '.zip') { throw 'Destination must have .zip extension' }
$allowedExtensions = @('.cs','.csproj','.md','.json','.config','.props','.ps1','.sh','.sln','.html','.css','.js','.mjs','.txt','.ttf','.woff','.woff2','.svg','.nupkg')
$allowedNames = @('.gitignore','.gitattributes','LICENSE')
$files = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Force | Where-Object {
    $relative = [System.IO.Path]::GetRelativePath($sourceRoot, $_.FullName)
    $segments = $relative -split '[\\/]'
    !($segments | Where-Object { $_ -in @('bin','obj','artifacts','lab-data','.local-appdata','.git') }) -and
    ($_.Extension.ToLowerInvariant() -in $allowedExtensions -or $_.Name -in $allowedNames) -and
    !(($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint))
} | Sort-Object FullName)
$stream = [System.IO.File]::Open($destination, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
try {
    $zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($file in $files) {
            $relative = [System.IO.Path]::GetRelativePath($sourceRoot, $file.FullName).Replace('\','/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, "SourceVault/$relative", [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
Write-Output "Packaged $($files.Count) source/document files. Build outputs, test fixtures, tooling profiles and private-key file types excluded."
