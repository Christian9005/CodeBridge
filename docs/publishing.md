# Publishing checklist

Everything is driven by one version: `<Version>` in `Directory.Build.props`. The NuGet packages, the VSIX manifest and
`toolbox-components.json` must match it (a unit test enforces this).

## One-time setup

1. **nuget.org**: create an account (turn on two-factor authentication) and note your profile name (not your e-mail). No API key is stored: the
   `Release` workflow uses *trusted publishing*, where GitHub proves which repository and workflow is pushing.
   - At <https://www.nuget.org/account/trustedpublishing> add a policy: owner `Christian9005`, repository `CodeBridge`, workflow file
     `release.yml` (the file name only, not the `name:` inside it), environment `release`.
   - In the GitHub repository: *Settings > Environments > New environment* `release`, and add the environment secret `NUGET_USER` = your nuget.org
     profile name. Optionally require yourself as a reviewer to get an approval gate before every publish.
   - The id `CodeBridge.Core` on nuget.org belongs to another project (as do `CodeBridge.Cli` and `CodeBridge.MSBuild`), so the package built from
     `src/CodeBridge.Core` is published as **`CodeBridge.Abstractions`**. The `CodeBridge.` prefix cannot be reserved for the same reason; the other
     nine ids were free when 0.6.0 was prepared. A package id on nuget.org is permanent: check `https://api.nuget.org/v3-flatcontainer/<id in lower case>/index.json`
     (404 means free) before adding a new package.
2. **Visual Studio Marketplace**: create a publisher at <https://marketplace.visualstudio.com/manage>. Two different names are involved, and the
   Marketplace checks both:
   - the publisher **ID** (`ChristianAlbarracin`) goes in `vs-publish.json` (`publisher`);
   - the publisher **display name** (`Christian Albarracin`, exactly as the Marketplace shows it) goes in `source.extension.vsixmanifest`
     (`Identity/@Publisher`). Anything else fails with `VsixPub0029: Publisher display name ... and Author name ... need to be the same`.

   Create an Azure DevOps PAT with scope *Marketplace > Manage* (all accessible organizations) and store it as the secret `VS_MARKETPLACE_PAT` of the
   `release` environment. The `<Tags>` of the manifest are separated by semicolons and each tag is at most 50 characters (`VsixPub0023`).
3. Optional: sign the VSIX (`tools/CodeBridge.VisualStudio.Signing`). Not required by the Marketplace.

## Each release

1. Bump `<Version>` (Directory.Build.props), `Identity/@Version` (vsixmanifest), `manifestVersion` and `package.version`
   (`Assets/toolbox-components.json`). Update `CHANGELOG.md`.
2. If the firmware changed: bump `FIRMWARE_VERSION`/`EXPECTED_FIRMWARE_VERSION`, rebuild with `pio run -d firmware/esp32-bridge` and copy
   `.pio/build/esp32/{firmware,bootloader,partitions}.bin` (and `boot_app0.bin`) to `firmware/prebuilt/esp32/`.
3. `./build/pack.ps1 -Vsix` then `dotnet test CodeBridge.slnx -c Release` and `./tests/smoke_test.ps1 -VsixPath artifacts/vsix/CodeBridge.VisualStudio.vsix`.
4. **Manual test on a clean machine / VS Experimental Instance**: install the VSIX, create the project, *Tools > CodeBridge Setup > Install*,
   add the starter form, build, flash a real ESP32, run Blink, open a `.cbflow` file in Light and Dark themes.
5. Merge the release branch into `master`, then tag that commit: `git tag vX.Y.Z && git push origin vX.Y.Z`. The `Release` workflow builds,
   tests, pushes the NuGet packages, publishes the VSIX and creates the GitHub release. A published NuGet version cannot be deleted (only
   unlisted), so if something is wrong fix it and publish the next version.
