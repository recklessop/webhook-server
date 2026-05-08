# Run As modes — when to use which

Each endpoint has a **Run As** setting (in the editor's "Run as" section) that controls *who* the script runs as. The default works for most cases, and switching modes is one dropdown change.

## The three modes

| Mode | Runs as | Use when… |
|---|---|---|
| **Service** *(default)* | Whoever the Windows Service runs under (LocalSystem by default) | Almost everything. Local file ops, calling local APIs, running cmd / PowerShell scripts that don't need a user identity. |
| **InteractiveUser** | The user logged in at the keyboard | The script needs to put a window on the screen (Calculator, a notification dialog, opening a browser tab) |
| **SpecificUser** | A named local or domain user / password you provide | The script runs in AD, a fileshare, or any system that wants the action attributed to a specific identity — and you don't want the service itself running as that user. |

## Service (default)

Nothing to configure. The hook runs as `LocalSystem` by default — full local rights, very limited network identity (the machine account on a domain).

You can change the service identity at install time via the `-ServiceAccount` parameter to `install-service.ps1` (gMSA, domain user, etc.). Anything you set there applies to **all** Service-mode endpoints. See [Service account & Active Directory](service-account-and-ad.md).

**Pros**: zero config per endpoint, no passwords to manage, fastest path
**Cons**: the script can't pop UI on the user's desktop (Session 0 isolation), and on a workgroup machine it has no domain identity at all

## InteractiveUser

Pick this when the hook should appear visually on the desktop of whoever is logged in. The clearest example is "fire a hook from my phone, get a Calculator window on my PC."

How it works internally: the service (running as SYSTEM) calls the Win32 API `WTSQueryUserToken` to grab the active console session's user token, then `CreateProcessAsUser` to land the new process inside that session.

What you don't have to configure: username, password, profile loading, session ID. All inferred at runtime.

What can go wrong:

- **No one logged in** at the keyboard → hook fails with `No active console session - is anyone logged in at the keyboard?`. The hook can't run; there's no desktop to land on.
- **Service runs as anything other than LocalSystem** → `WTSQueryUserToken` requires SYSTEM. If you switched the service to a gMSA / domain user, InteractiveUser stops working.
- **Locked desktop, no user logged in but session 1 reserved** → similar to "no one logged in." Once a user logs in interactively (even just to the lock screen with credentials cached), the session is "active enough" for this to work.

**Use case examples**: see [recipes/ui-on-desktop.md](recipes/ui-on-desktop.md).

## SpecificUser

Pick this when the hook needs to authenticate as a specific account — a service account with delegated AD rights, a local Administrator on a remote machine, etc. — but you don't want the *whole service* running as that account.

Configure:

- **Username**: `DOMAIN\user`, `.\local-user`, or a UPN like `user@contoso.com`. The leading `.\` is shorthand for the local machine.
- **Password**: stored DPAPI-encrypted at rest. Visible in plaintext in the GUI for an admin user, by design — anyone with admin pipe access already has SYSTEM-equivalent rights.
- **Load profile**: optional. Loads the user's HKCU and AppData before running. Slower (~1s extra). Only needed if the script reads user-scoped settings (uncommon).

How it works internally: the service calls `LogonUser` with the credentials (interactive logon type first, falls back to batch logon for service-only accounts), then `DuplicateTokenEx` + `CreateProcessAsUser`. The script lands in a fresh batch session with the user's network identity.

> **Why not `psi.UserName` / `psi.Password` like a normal .NET app?** Because `CreateProcessWithLogonW` (what those properties use under the hood) refuses to run when the caller is `LocalSystem`, which is exactly our scenario. The token-based path is the documented Windows mechanism for this.

What can go wrong:

- **Wrong password** → log shows `LogonUser (DOMAIN\user) failed - The user name or password is incorrect`. Re-enter in the editor.
- **Account is denied logon locally** → log shows `Logon failure: the user has not been granted the requested logon type`. Make sure the account has at least one of *Log on as a batch job* or *Log on locally* under `secpol.msc` → Local Policies → User Rights Assignment.
- **Domain controller unreachable** → for domain accounts, the service must be able to reach a DC. For local accounts (`.\name`), no domain dependency.

## Decision flowchart

```
                       Need UI on the user's desktop?
                                    │
                  ┌─────── yes ─────┴────── no ─────┐
                  │                                  │
        InteractiveUser                Need specific identity (AD / fileshare / etc.)?
                                                     │
                                       ┌──── yes ────┴──── no ────┐
                                       │                            │
                                  Should ALL hooks run as            Service
                                  this identity?
                                       │
                  ┌────── yes ──────────┴───────── no ──────────┐
                  │                                              │
       Run service itself                              SpecificUser per endpoint
       as that account
       (gMSA / domain user)
       see service-account-and-ad.md
```
