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

.EXAMPLE
    ./build/publish.ps1
    ./build/publish.ps1 -CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
#>
param(
    [string] $CertificateThumbprint,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [string] $Runtime = 'win-x64',
    [string] $Output = (Join-Path $PSScriptRoot '..\artifacts\publish')
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

Write-Host '== Restore (locked) and test' -ForegroundColor Cyan
dotnet restore $root --locked-mode
if ($LASTEXITCODE) { throw 'Restore failed: packages.lock.json is out of date or a package failed audit/signature checks.' }
dotnet test $root -c Release --no-restore
if ($LASTEXITCODE) { throw 'Tests failed.' }

Write-Host '== Publish' -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src\JiraTaskBar.App') -c Release -r $Runtime --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:ContinuousIntegrationBuild=true `
    -o $Output
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

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
Set-Content -Path "$exe.sha256" -Value "$hash  JiraTaskBar.exe"
Write-Host "Done: $exe`nSHA-256: $hash" -ForegroundColor Green
