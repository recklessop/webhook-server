# Recipe: Pop UI on the user's desktop

The classic "fire a hook from your phone, see a calculator window appear on your PC." Useful for:

- Triggering interactive installers / wizards
- Opening browser tabs to specific dashboards on demand
- Playing a sound / showing a toast notification
- Demos and party tricks

## Why this is non-trivial on Windows

The Webhook Server service runs as `LocalSystem` in **session 0**. Anything launched normally from a Service-mode endpoint also lands in session 0, which has no visible desktop — UI runs but nobody sees it. To put a window on the desktop of whoever is logged in at the keyboard, the service has to:

1. Find the active console session ID (`WTSGetActiveConsoleSessionId`)
2. Get a primary token for the user in that session (`WTSQueryUserToken`)
3. Spawn the new process with `CreateProcessAsUser` against that token, targeting `winsta0\default`

Webhook Server does all of this for you when the endpoint's **Run as** is set to **InteractiveUser**.

## Configure the endpoint

| Section | Setting | Value |
|---|---|---|
| Identity | Slug | `calc` |
| Identity | Description | "Pop calculator on the logged-in user's desktop" |
| Auth | Mode | None / Bearer — your call |
| Allowed clients | | restrict; this is interactive UI |
| Executor | Type | **Executable** |
| Executor | Executable path | `C:\Windows\System32\calc.exe` |
| Run as | Identity | **InteractiveUser** |
| Response | Mode | **Async** *(calc never exits on its own; sync would 30-second-timeout-kill it every time)* |
| Response | Fail on non-zero exit | unticked |

Save. Hit `http://localhost:8080/hook/calc` from anywhere — calc.exe pops up on your desktop.

## Limits

- **Service must run as LocalSystem.** Only SYSTEM has the `SeTcbPrivilege` required for `WTSQueryUserToken`. If you switched the service to a gMSA (e.g. for AD-write hooks), this mode stops working. Run two instances of Webhook Server on different ports if you need both.
- **Someone must be logged in** at the console. If the desktop is at the lock screen with no user signed in, the hook fails with `No active console session - is anyone logged in at the keyboard?`.
- **RDP sessions complicate things.** `WTSGetActiveConsoleSessionId` always returns the *console* session, not RDP sessions. If only RDP users are connected and no one is at the physical keyboard, this mode fails. (A separate API, `WTSQueryUserToken` against an enumerated session ID, can target RDP — that'd be a v0.x feature request.)
- **Multiple users logged in via fast-user-switching** — the hook lands in whichever session is currently active (the foreground desktop), not all of them.

## Variations

### Notification toast instead of a window

Use a PowerShell script that emits a Windows 10/11 toast via `BurntToast` (third-party module) or the built-in WinRT API:

```powershell
# requires: Install-Module BurntToast
New-BurntToastNotification -Text 'Webhook fired',$($input | Out-String)
```

Configure the endpoint as InteractiveUser + WindowsPowerShell + inline command. The toast appears as the logged-in user — same as if they fired it themselves.

### Open a URL in the user's default browser

```powershell
Start-Process ($input | ConvertFrom-Json).url
```

Body: `{ "url": "https://contoso.servicenow.com/incident/123" }`

This opens the URL in whatever the user has set as default. Handy for "page on-call → they reply on their phone with a link → URL opens on their workstation when they sit down."

### Run a setup wizard / installer that needs UI

Some installers refuse to run silently or have steps that require human input. Wrap them as InteractiveUser hooks so the operator can trigger them from a help-desk console without having to RDP in.
