# Service account & Active Directory

The service runs as `LocalSystem` out of the box. That's right for local-only scripts and **read-only** AD queries (LocalSystem authenticates to the network as the machine account, which Authenticated Users includes by default). It is wrong for hooks that need to **modify** AD — passwords, group memberships, computer objects.

This page covers the four real-world choices and how to switch.

## The four options

| Account | Network identity | When to use |
|---|---|---|
| **`LocalSystem`** *(default)* | Computer account `DOMAIN\MACHINE$` on a domain-joined host; nothing on a workgroup host | Default. Local file ops, simple PowerShell, read-only AD queries. Most powerful local account — any hook running under it has full local rights. |
| **`LocalService`** | None | **Don't.** Cannot talk to a domain controller. Listed only to rule it out. |
| **`NetworkService`** | Same machine account as LocalSystem | Slightly less local privilege than LocalSystem, same network identity. Rarely the right pick. |
| **Domain user** (`DOMAIN\svc-webhookserver`) | That user | Use when hooks need write access to AD and you can't use a gMSA. You own password rotation. |
| **gMSA** (`DOMAIN\svc-webhookserver$`) | That gMSA | **Recommended for AD-write workloads.** AD generates and rotates the password automatically every 30 days. Requires domain functional level 2012+. |

## Switching the service account at install time

Pass `-ServiceAccount` to `install-service.ps1` (or to the deploy / dev launcher):

```powershell
# Domain user
& "C:\Program Files\WebhookServer\scripts\install-service.ps1" `
    -BinaryPath "C:\Program Files\WebhookServer\WebhookServer.Service.exe" `
    -ServiceAccount "CONTOSO\svc-webhookserver" -Password "..."

# gMSA - note trailing $ and no -Password
& "C:\Program Files\WebhookServer\scripts\install-service.ps1" `
    -BinaryPath "C:\Program Files\WebhookServer\WebhookServer.Service.exe" `
    -ServiceAccount 'CONTOSO\svc-webhookserver$'
```

Or do it manually with `sc.exe` if the service is already installed:

```powershell
sc.exe stop WebhookServer
sc.exe config WebhookServer obj= 'CONTOSO\svc-webhookserver$'
sc.exe start WebhookServer
```

## gMSA setup (recommended for AD writes)

A gMSA is a Group Managed Service Account. Active Directory generates and stores its password and rotates it every 30 days; the host machine account retrieves the password as needed. You never see or store it. This is the cleanest pattern for production.

### One-time domain setup

If your domain has never used gMSAs, create the KDS root key (only needed once per domain):

```powershell
# from a Domain Admin PowerShell, on any DC
Add-KdsRootKey -EffectiveImmediately
# in production wait 10 hours for replication; in a lab, override:
# Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10))
```

### Create the gMSA

```powershell
# from a DC, with AD PowerShell module loaded
New-ADServiceAccount -Name svc-webhookserver `
    -DNSHostName webhook01.contoso.local `
    -PrincipalsAllowedToRetrieveManagedPassword "DOMAIN\WebhookHosts"
```

`PrincipalsAllowedToRetrieveManagedPassword` is the security group containing the computer accounts allowed to use the gMSA. Add your webhook host(s) to that group:

```powershell
Add-ADGroupMember -Identity 'WebhookHosts' -Members 'WEBHOOK01$'
# the host needs to reboot OR have its kerberos ticket flushed for the new group membership to apply
```

### Install the gMSA on the host

On the webhook server machine itself:

```powershell
# from elevated PowerShell, AD PowerShell module installed (RSAT)
Install-ADServiceAccount svc-webhookserver
Test-ADServiceAccount svc-webhookserver  # should return True
```

If `Test-ADServiceAccount` returns False, check:

- Host is in the `WebhookHosts` group (or whoever's in `PrincipalsAllowedToRetrieveManagedPassword`)
- Host has been rebooted since being added to the group
- KDS root key has had time to propagate (10 hours by default)

### Configure the service to use it

```powershell
# from elevated PowerShell on the webhook host
sc.exe stop WebhookServer
sc.exe config WebhookServer obj= 'CONTOSO\svc-webhookserver$'
sc.exe start WebhookServer
```

Note the trailing `$`. There is **no password parameter** for gMSAs. The trailing `$` is what tells the SCM "look up this account in AD as a managed service account, retrieve its password automatically."

### Grant AD permissions

Give the gMSA only what it needs. For a typical "reset user passwords" workload:

```powershell
# Delegate "Reset password and force change at next logon" on a specific OU
$ou = "OU=Standard Users,DC=contoso,DC=local"
dsacls $ou /I:S /G "CONTOSO\svc-webhookserver$:CA;Reset Password;user"
dsacls $ou /I:S /G "CONTOSO\svc-webhookserver$:WP;pwdLastSet;user"
```

…or use the GUI Delegation of Control wizard in Active Directory Users and Computers.

## Domain user (fallback when gMSA isn't available)

```powershell
# 1. Create the user (one time)
New-ADUser -Name "svc-webhookserver" -SamAccountName "svc-webhookserver" `
    -AccountPassword (Read-Host -AsSecureString "password") -Enabled $true `
    -PasswordNeverExpires $true -CannotChangePassword $true

# 2. Grant "Log on as a service" right on the host:
#    secpol.msc -> Local Policies -> User Rights Assignment -> Log on as a service
#    Add CONTOSO\svc-webhookserver

# 3. Configure the service:
sc.exe config WebhookServer obj= "CONTOSO\svc-webhookserver" password= "..."
```

You own password rotation. When you change the password in AD, also update the service via `sc.exe config WebhookServer password= "newpw"` and restart it.

## What changes for hooks when you switch the service account

- **Service mode hooks** now run as the new account. PowerShell `whoami` from inside a hook will show the new identity.
- **InteractiveUser hooks stop working** if you switch off LocalSystem. Only SYSTEM can call `WTSQueryUserToken`. If you need both AD-write hooks and UI-on-desktop hooks, pick one of:
  - Keep service as LocalSystem and use **SpecificUser** mode for AD-write hooks
  - Switch service to a gMSA / domain user and drop UI hooks (or move them to a separate Webhook Server instance running as LocalSystem)
- **SpecificUser hooks** continue to work regardless. They use a separate `LogonUser` token per call.

## Verifying the switch worked

After changing the service account, restart the service and add a quick diagnostic endpoint:

```
slug:           whoami
auth:           none
executor:       Windows PowerShell
inline command: whoami; whoami /groups
```

Hit it and verify the output matches the account you configured. The first line should be `domain\svc-webhookserver` (or `domain\machine$` for LocalSystem on a domain-joined host).
