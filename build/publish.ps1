<#
.SYNOPSIS
    Builds a release of Jira TaskBar: tests, locked restore, single-file publish, optional code signing.

.DESCRIPTION
    Threat model E2/E3/S4: releases restore from the lock files only, run the tests, and can be
    Authenticode-signed so users and SmartScreen can tell a genuine build from a tampered one.
    Install the output under %ProgramFiles% (admin-writable) so other same-user processes can't
    replace the exe that the "Start with Windows" entry points at.

.PARAMETER CertificateThumbprint
    Thumbprint of a code-signing certificate in Cert:\CurrentUser\My. Omit for an unsigned build.

.PARAMETER TimestampUrl
    RFC 3161 timestamp server, so the signature stays valid after the certificate expires.

.PARAMETER Version
    Optional semantic version to embed in the executable (for example 1.2.0 or 1.2.0-beta.1).
    Release automation derives this from the Git tag. Local builds use the project version when omitted.

.EXAMPLE
    ./build/publish.ps1
    ./build/publish.ps1 -CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
#>
param(
    [string] $CertificateThumbprint,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [string] $Runtime = 'win-x64',
    [string] $Version,
    [string] $Output = (Join-Path $PSScriptRoot '..\artifacts\publish')
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$Output = [IO.Path]::GetFullPath($Output)
if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
    throw "Version '$Version' is not a supported semantic version."
}
# Start clean so files from an older publish (e.g. stale .pdb/.xml) never ship.
if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }

Write-Host '== Restore (locked) and test' -ForegroundColor Cyan
dotnet restore $root --locked-mode
if ($LASTEXITCODE) { throw 'Restore failed: packages.lock.json is out of date or a package failed audit/signature checks.' }
dotnet test $root -c Release --no-restore
if ($LASTEXITCODE) { throw 'Tests failed.' }

Write-Host '== Publish' -ForegroundColor Cyan
# One compressed exe: symbols embedded (readable crash stacks), no package XML docs alongside.
$publishArguments = @(
    'publish', (Join-Path $root 'src\JiraTaskBar.App'),
    '-c', 'Release', '-r', $Runtime, '--self-contained',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true', '-p:DebugType=embedded',
    '-p:AllowedReferenceRelatedFileExtensions=none', '-p:ContinuousIntegrationBuild=true',
    '-o', $Output
)
if ($Version) {
    $publishArguments += "-p:Version=$Version"
}
dotnet @publishArguments
if ($LASTEXITCODE) { throw 'Publish failed.' }

$exe = Join-Path $Output 'JiraTaskBar.exe'
if ($CertificateThumbprint) {
    Write-Host '== Sign' -ForegroundColor Cyan
    $cert = Get-Item "Cert:\CurrentUser\My\$CertificateThumbprint"
    $result = Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -TimestampServer $TimestampUrl -HashAlgorithm SHA256
    if ($result.Status -ne 'Valid') { throw "Signing failed: $($result.StatusMessage)" }
} else {
    Write-Warning 'No -CertificateThumbprint given: the exe is UNSIGNED. Sign it before distributing to others.'
}

Write-Host '== Smoke test packaged executable' -ForegroundColor Cyan
$process = Start-Process -FilePath $exe -ArgumentList '--smoke-test' -PassThru
if (-not $process.WaitForExit(30000)) {
    Stop-Process -Id $process.Id -Force
    throw 'Packaged executable smoke test timed out after 30 seconds.'
}
if ($process.ExitCode -ne 0) {
    throw "Packaged executable smoke test failed with exit code $($process.ExitCode)."
}

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
Set-Content -Path "$exe.sha256" -Value "$hash  JiraTaskBar.exe"
Write-Host "Done: $exe`nSHA-256: $hash" -ForegroundColor Green
