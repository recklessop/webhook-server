# Webhook Server documentation

Webhook Server is a Windows service that runs a script (PowerShell, cmd, or any executable) when an HTTP request hits a URL you choose. It's designed for sysadmins who want to wire a tool like **Zerto pre/post scripts**, GitHub Actions, a monitoring system, or a backup tool into a Windows-side automation step — without writing a custom listener every time.

## New here? Start with these

1. [Concepts](concepts.md) — five-minute read on what a webhook is and how this server uses one
2. [Installation](installation.md) — download, install, first endpoint
3. [Recipe: Zerto failover post-script → DNS + service checks](recipes/zerto-pre-post-scripts.md) — the canonical reason this exists

## Topical

- [Upgrading](upgrading.md)
- [Uninstalling](uninstalling.md)
- [Run As modes — when to use which](runas-modes.md)
- [Service account & Active Directory](service-account-and-ad.md)
- [Network & security](network-and-security.md)
- [Troubleshooting](troubleshooting.md)

## Recipes (cookbook style)

- [Zerto failover post-script → DNS + service checks](recipes/zerto-pre-post-scripts.md) ← canonical use case
- [GitHub-style HMAC-signed webhook](recipes/github-style-hmac.md)
- [Pop UI on the user's desktop](recipes/ui-on-desktop.md)

The flagship Zerto recipe also ships with a **ready-to-use Zerto-side post-script** at [`scripts/examples/zerto-post-failover.ps1`](../scripts/examples/zerto-post-failover.ps1).

## Reference

- [GitHub repo](https://github.com/recklessop/webhook-server)
- [Latest release](https://github.com/recklessop/webhook-server/releases/latest)
- [Issue tracker](https://github.com/recklessop/webhook-server/issues)
