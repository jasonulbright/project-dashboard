# Releasing Project Dashboard

Version tags use `v2.3.N`. Update the project version, NSIS version, and changelog together. Published releases and their assets are immutable.

The Release workflow runs under the GitHub `release` environment. Azure's federated credential `release-project-dashboard` trusts the subject `repo:jasonulbright/project-dashboard:environment:release`. This is the authentication credential name; the certificate profile is `SRL-Public` in account `signalridgelabs`, at `https://eus.codesigning.azure.net`. The workflow consumes `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and `AZURE_SUBSCRIPTION_ID` from GitHub secrets. These are the same account and certificate profile used by the other desktop releases. No signing key is downloaded.

The workflow builds and runs every test, publishes the application payload, exports the NSIS uninstaller, and signs the first-party executable, assembly, and uninstaller. It packages those signed bytes into the portable archive and installer, then signs the final installer. Third-party assemblies retain their upstream signatures.

Before publication, `installer/verify-release.ps1` requires valid Authenticode signatures with publisher `Jason Ulbright` and timestamps, compares every portable and installed file to the payload, checks versions and notices, and runs installation, application startup, and uninstallation on the disposable Windows runner. Checksums are computed after signing. The verified installer, portable archive, and checksums are also saved as a workflow artifact.

For a rehearsal, dispatch **Release** on the desired ref with `dry_run=true`. It performs signing and all verification without creating a GitHub release. Publishing from manual dispatch requires a version tag; a branch is refused. After the full local build and test gate, push the release commit and its lightweight version tag and watch the workflow through publication.

Local packaging without Azure authentication:

```powershell
dotnet build --configuration Release -warnaserror
dotnet test --configuration Release --no-build
pwsh -File installer/build-portable.ps1 -PublishOnly
Push-Location installer
makensis /DEXPORT_UNINST project-dashboard.nsi
makensis /DIMPORT_UNINST project-dashboard.nsi
Pop-Location
pwsh -File installer/build-portable.ps1 -SkipPublish
pwsh -File installer/verify-release.ps1 -Version 2.3.N -SkipSignatureCheck
```

Substitute the actual version for `2.3.N`. These local outputs are unsigned. The signature bypass is for local package checks only; the Release workflow always requires signatures. Install/uninstall smoke testing runs only on disposable GitHub Actions runners because it writes shortcuts and per-user installation registry entries.

The two-pass uninstaller build follows the [NSIS external signing workflow](https://nsis.sourceforge.io/Signing_an_Uninstaller_externally). Authentication and signing use the [Azure Artifact Signing action](https://github.com/Azure/artifact-signing-action).
