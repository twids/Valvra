#Requires -RunAsAdministrator
#Requires -Modules WebAdministration
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishedPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9.-]+$')][string]$HostName,
    [Parameter(Mandatory)][string]$HttpsCertificateThumbprint,
    [Parameter(Mandatory)][string]$ServiceAccount,
    [string]$InstallRoot = 'C:\inetpub\Valvra',
    [string]$SiteName = 'Valvra',
    [string]$ApplicationPool = 'Valvra'
)
$ErrorActionPreference = 'Stop'
Import-Module WebAdministration
$sourceRoot = (Resolve-Path -LiteralPath $PublishedPath).Path
$destinationRoot = [System.IO.Path]::GetFullPath($InstallRoot)
if ($destinationRoot -eq [System.IO.Path]::GetPathRoot($destinationRoot)) { throw 'InstallRoot cannot be a drive root.' }
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'Valvra.Web.exe'))) { throw 'Publish the win-x64 application before installation.' }
if (Test-Path -LiteralPath $destinationRoot) { throw 'InstallRoot already exists. Use the documented upgrade procedure instead of overwriting it.' }
if (Test-Path "IIS:\Sites\$SiteName") { throw 'The IIS website already exists.' }
if (Test-Path "IIS:\AppPools\$ApplicationPool") { throw 'The IIS application pool already exists.' }
$httpsCertificate = Get-Item -LiteralPath "Cert:\LocalMachine\My\$($HttpsCertificateThumbprint.Replace(' ',''))"
if (-not $httpsCertificate.HasPrivateKey) { throw 'HTTPS certificate has no private key.' }
if ($httpsCertificate.NotAfter -le (Get-Date)) { throw 'HTTPS certificate has expired.' }
$account = New-Object System.Security.Principal.NTAccount($ServiceAccount)
$null = $account.Translate([System.Security.Principal.SecurityIdentifier])
$isGmsa = $ServiceAccount.EndsWith('$')
$servicePassword = ''
if (-not $isGmsa) {
    $credential = Get-Credential -UserName $ServiceAccount -Message 'Credentials for the IIS service identity (prefer gMSA).'
    if ($null -eq $credential) { throw 'Service identity credentials are required.' }
    $servicePassword = $credential.GetNetworkCredential().Password
}

New-Item -ItemType Directory -Path $destinationRoot | Out-Null
Get-ChildItem -LiteralPath $sourceRoot -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $destinationRoot -Recurse }
$settingsFile = Join-Path $destinationRoot 'appsettings.json'
$settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
$settings.AllowedHosts = $HostName
[System.IO.File]::WriteAllText($settingsFile, ($settings | ConvertTo-Json -Depth 20), (New-Object System.Text.UTF8Encoding($false)))
$configurationRoot = Join-Path $destinationRoot 'App_Data'
New-Item -ItemType Directory -Path $configurationRoot -Force | Out-Null

function Set-DirectoryAcl([string]$Path, [System.Security.AccessControl.FileSystemRights]$ServiceRights) {
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inheritance = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
        $identity = New-Object System.Security.Principal.SecurityIdentifier($sid)
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', $inheritance, 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($account, $ServiceRights, $inheritance, 'None', 'Allow')
    $acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $Path -AclObject $acl
}
Set-DirectoryAcl $destinationRoot ([System.Security.AccessControl.FileSystemRights]::ReadAndExecute)
Set-DirectoryAcl $configurationRoot ([System.Security.AccessControl.FileSystemRights]::Modify)

function Grant-PrivateKeyRead($Certificate) {
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
    try {
        if ($rsa -is [System.Security.Cryptography.RSACng]) {
            $keyPath = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$($rsa.Key.UniqueName)"
        } elseif ($rsa -is [System.Security.Cryptography.RSACryptoServiceProvider]) {
            $keyPath = Join-Path $env:ProgramData "Microsoft\Crypto\RSA\MachineKeys\$($rsa.CspKeyContainerInfo.UniqueKeyContainerName)"
        } else { throw 'Unsupported private key provider.' }
        $keyAcl = Get-Acl -LiteralPath $keyPath
        $keyAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($account, 'Read', 'Allow')))
        Set-Acl -LiteralPath $keyPath -AclObject $keyAcl
    } finally { if ($null -ne $rsa) { $rsa.Dispose() } }
}
$encryption = New-SelfSignedCertificate -Subject 'CN=Valvra Encryption' -CertStoreLocation Cert:\LocalMachine\My `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyUsage KeyEncipherment -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears(5) -Type Custom
$audit = New-SelfSignedCertificate -Subject 'CN=Valvra Audit Signing' -CertStoreLocation Cert:\LocalMachine\My `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyUsage DigitalSignature -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears(5) -Type Custom
Grant-PrivateKeyRead $encryption
Grant-PrivateKeyRead $audit

New-WebAppPool -Name $ApplicationPool | Out-Null
Set-ItemProperty "IIS:\AppPools\$ApplicationPool" -Name managedRuntimeVersion -Value ''
Set-ItemProperty "IIS:\AppPools\$ApplicationPool" -Name processModel -Value @{ identityType = 3; userName = $ServiceAccount; password = $servicePassword; loadUserProfile = $true; maxProcesses = 1 }
$servicePassword = $null
New-Website -Name $SiteName -PhysicalPath $destinationRoot -ApplicationPool $ApplicationPool -Port 443 -HostHeader $HostName -Ssl | Out-Null
Set-WebBinding -Name $SiteName -BindingInformation "*:443:$HostName" -PropertyName sslFlags -Value 1
(Get-WebBinding -Name $SiteName -Protocol https).AddSslCertificate($httpsCertificate.Thumbprint, 'My')
Set-WebConfigurationProperty -Filter '/system.webServer/security/authentication/anonymousAuthentication' -Name enabled -Value $false -Location $SiteName
Set-WebConfigurationProperty -Filter '/system.webServer/security/authentication/windowsAuthentication' -Name enabled -Value $true -Location $SiteName
Set-WebConfigurationProperty -Filter '/system.webServer/security/authentication/windowsAuthentication' -Name useAppPoolCredentials -Value $true -Location $SiteName
# Protect configuration/private backup extensions independently of the application's static-file settings.
$hidden = Get-WebConfiguration -Filter '/system.webServer/security/requestFiltering/hiddenSegments/add' -Location $SiteName
if (-not ($hidden | Where-Object { $_.segment -eq 'App_Data' })) {
    Add-WebConfigurationProperty -Filter '/system.webServer/security/requestFiltering/hiddenSegments' -Name '.' -Value @{ segment = 'App_Data' } -Location $SiteName
}

Push-Location $destinationRoot
try {
    & (Join-Path $destinationRoot 'Valvra.Web.exe') --initialize-setup
    if ($LASTEXITCODE -ne 0) { throw 'Setup code initialization failed.' }
} finally { Pop-Location }
Write-Host "Encryption certificate: $($encryption.Thumbprint)"
Write-Host "Audit signing certificate: $($audit.Thumbprint)"
Write-Host "Open https://$HostName/setup and complete installation. Back up the two private keys separately before production use."
