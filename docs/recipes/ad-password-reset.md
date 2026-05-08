# Recipe: AD password reset endpoint

A self-service password reset URL your help-desk tool can hit. Single endpoint, gMSA-backed, audited.

## Architecture

- The webhook host is domain-joined
- The service runs as a gMSA with **Reset Password** + **Write pwdLastSet** delegated on the OUs containing target users
- The endpoint is HMAC-signed, IP-allowlisted to the help-desk app's server
- Every reset is logged in the daily log file with caller IP, target user, runId, and result

## Prerequisites

- gMSA created and installed on the host. See [Service account & Active Directory](../service-account-and-ad.md).
- Service installed with `-ServiceAccount 'CONTOSO\svc-webhookserver$'`
- Delegate the right permissions on the OU(s):

```powershell
$ou = "OU=Standard Users,DC=contoso,DC=local"
dsacls $ou /I:S /G "CONTOSO\svc-webhookserver$:CA;Reset Password;user"
dsacls $ou /I:S /G "CONTOSO\svc-webhookserver$:WP;pwdLastSet;user"
```

## The script

`C:\Scripts\ad-password-reset.ps1`:

```powershell
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Import-Module ActiveDirectory

$body = $input | ConvertFrom-Json

if (-not $body.samAccountName) { throw 'samAccountName is required' }
if (-not $body.newPassword)    { throw 'newPassword is required' }
if (-not $body.requestedBy)    { throw 'requestedBy is required (audit field)' }

# Refuse to touch privileged groups
$user = Get-ADUser -Identity $body.samAccountName -Properties MemberOf
$denyGroups = @('Domain Admins','Enterprise Admins','Schema Admins')
foreach ($g in $user.MemberOf) {
    $name = ($g -split ',')[0] -replace '^CN='
    if ($denyGroups -contains $name) {
        throw "refusing to reset password for member of $name"
    }
}

$secure = ConvertTo-SecureString $body.newPassword -AsPlainText -Force
Set-ADAccountPassword -Identity $user -NewPassword $secure -Reset
Set-ADUser -Identity $user -ChangePasswordAtLogon $true

# Audit line goes to the webhook log automatically (return value becomes stdout).
"reset $($user.SamAccountName) requested by $($body.requestedBy)"
```

## Endpoint configuration

| Section | Setting | Value |
|---|---|---|
| Identity | Slug | `ad-reset` |
| Auth | Mode | **HMAC** with a strong secret shared with the help-desk app |
| Auth | HMAC header | `X-Signature-256` |
| Auth | HMAC prefix | `sha256=` |
| Auth | HMAC encoding | hex |
| Allowed clients | | `10.50.10.20` *(the help-desk app's IP only)* |
| Executor | Type | Windows PowerShell |
| Executor | Script path | `C:\Scripts\ad-password-reset.ps1` |
| Data passing | JSON body to stdin | ✓ |
| Data passing | Headers/query as env vars | ✗ |
| Run as | Identity | **Service** *(uses the gMSA)* |
| Response | Mode | Sync |
| Response | Timeout (sec) | 30 |
| Response | Fail on non-zero exit | ✓ |

## Calling it

```powershell
$body = @{
    samAccountName = 'jdoe'
    newPassword    = 'TempP@ssw0rd!2026'
    requestedBy    = 'helpdesk_user@contoso.local'
} | ConvertTo-Json

$bytes = [Text.Encoding]::UTF8.GetBytes($body)
$hmac  = [Security.Cryptography.HMACSHA256]::new(
    [Text.Encoding]::UTF8.GetBytes('your-shared-secret'))
$sig   = ([BitConverter]::ToString($hmac.ComputeHash($bytes)) -replace '-','').ToLower()

Invoke-RestMethod -Method POST `
    -Uri 'http://webhooks.contoso.local:8080/hook/ad-reset' `
    -Headers @{ 'X-Signature-256' = "sha256=$sig" } `
    -ContentType 'application/json' -Body $body
```

## Operational notes

**Audit log**: every call lands in `C:\ProgramData\WebhookServer\logs\webhook-YYYYMMDD.log` with one line per run including the runId, slug, caller IP, exit code, and the script's stdout (the `"reset jdoe requested by helpdesk_user"` line). Ship those logs to your SIEM via the usual file-collector flow.

**Rotating the HMAC secret**: edit the endpoint in the GUI, replace the secret, save. The help-desk app needs the new secret too — coordinate the cutover. There's no overlap window built in; if you need a soft rollover, create a second endpoint with the new secret and switch caller traffic over.

**Privileged-group guard**: the script's `denyGroups` check is a basic guard. If a more sophisticated guard is needed (target user attribute, OU-based logic), add it in the script — that's the right place, not the webhook server.

**Self-service from the user side**: don't expose this endpoint to end users directly. Front it with a help-desk app that authenticates the user (preferably with MFA), then makes the call to the webhook with its bearer/HMAC credentials. The webhook server is the *plumbing*; not the *front door*.
