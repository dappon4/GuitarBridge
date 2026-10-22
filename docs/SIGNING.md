# Signing Windows downloads

Both GuitarBridge.exe and the MSI can carry an Authenticode signature.
Current releases are unsigned until a maintainer configures a trusted signing
provider. SHA-256 checksums detect damaged downloads; they are not publisher
signatures. Do not describe an unsigned build as signed or ask users to trust
a self-signed root certificate for public distribution.

Options include:

- [SignPath Foundation](https://signpath.org/): free signing for approved open
  source projects, subject to an application and its policies. Its certificate
  uses SignPath Foundation as publisher and every signing request requires
  approval by a designated project maintainer. Approval is not guaranteed.
- [Azure Artifact Signing](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart):
  a paid service with identity validation and CI integration. Individual public
  trust accounts are currently limited to the United States and Canada; check
  the current eligibility requirements before subscribing.
- A publicly trusted code-signing certificate with a hardware token or cloud
  signing service. A certificate file without access to its private key cannot
  sign a release.

With a provider configured, the build order is:

1. Publish the self-contained EXE and run the automated checks.
2. Authenticode-sign `artifacts/publish/GuitarBridge.exe`, with an RFC 3161
   timestamp, and verify it with `signtool verify /pa /all /v`.
3. Build the MSI from the signed executable. Packaging also copies that signed
   EXE to the portable download.
4. Sign and verify the MSI, then regenerate `SHA256SUMS.txt` over the final
   signed EXE and MSI. Run the install/uninstall checks against these files.
5. Upload the final signed artifacts to the GitHub Release.

The update client supports signed releases without a different download format.
Keep signing credentials out of this repository and client app. Use the chosen
provider's authenticated CI integration (prefer OIDC where supported), scoped
to release builds. No signing account, paid subscription, or certificate is
created by this repository's workflow.

A trusted signature identifies the publisher and protects file integrity.
SmartScreen reputation is separate, so new signed builds can still show
warnings. See [Microsoft's current signing comparison](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options).
