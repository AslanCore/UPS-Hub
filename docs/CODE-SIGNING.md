# Code signing (Authenticode)

UPS Hub releases are protected by two separate things:

1. **Update signature (always on).** `SHA256SUMS.txt` is signed with the UPS Hub minisign key. The app refuses to run a downloaded installer unless that signature and the installer's SHA-256 both check out. This works without any certificate and costs nothing.
2. **Authenticode (optional).** This is a Windows code-signing certificate on `UpsHub.exe` and `UpsHub-Setup-x.y.z.exe`. Without it, Windows SmartScreen shows "Windows protected your PC" on the first download until the file builds up reputation. The *publisher* shows as "Unknown".

Ways to get Authenticode, cheapest first:

| Option | Cost | Notes |
|---|---|---|
| **SignPath Foundation** (signpath.org) | free for open-source projects | Requires a public repository, a reproducible build in CI, and an application/review. The certificate is issued to SignPath Foundation, and signing runs from GitHub Actions through their connector. |
| **Azure Trusted Signing** | about 10 USD a month (Basic) | Microsoft-managed certificate, with identity validation for an individual or an organisation. It plugs into GitHub Actions with `azure/trusted-signing-action`; `ci/release.yml` already has the optional steps, enabled by the `TRUSTED_SIGNING_ACCOUNT` secret. |
| OV certificate from a CA (Sectigo, SSL.com, Certum, …) | about 100–400 USD a year | Since 2023, the key must live on a hardware token or a cloud HSM, which makes CI signing harder and adds cost. |
| EV certificate | more | It no longer skips SmartScreen reputation (changed in 2024), so it's rarely worth the premium for this app. |

The Azure Trusted Signing setup for `release.yml`:
1. Create a Trusted Signing account and certificate profile (public trust) in the Azure portal, and complete identity validation.
2. Create an app registration (service principal) with the *Trusted Signing Certificate Profile Signer* role on the account.
3. Add these repository secrets: `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`, `TRUSTED_SIGNING_ACCOUNT`, `TRUSTED_SIGNING_PROFILE`.
4. Check the `endpoint:` region in the workflow (it defaults to West Europe).

The workflow signs `UpsHub.exe` before it is packed into the installer, then signs the installer. It signs both before the checksums are made, so the checksums match the signed files.

Nothing here has been set up yet. Current builds are not Authenticode-signed.
