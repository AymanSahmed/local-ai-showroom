# Local NuGet feed (optional)

Only needed for the optional **Aion Instruct Preview** lane.

1. Download `AionInstructPreview.Text.Framework.1.1.0.nupkg` and the framework MSIX for your device
   (ARM64 Snapdragon or x64 Intel Lunar Lake) from the releases of
   https://github.com/microsoft/Aion-Instruct-Preview-Sample — review the pre-release EULA first.
2. Install the framework: `Add-AppxPackage .\AionInstructPreview.LanguageModel.Framework_1.1.0.0_<arch>.msix`
3. Copy the `.nupkg` into this folder.
4. Run the bridge with `dotnet run -p:EnableAion=true` (or `scripts\start-demo.ps1 -Aion`).

The preview is for developer validation only. Do not hand it to customers or end users.
