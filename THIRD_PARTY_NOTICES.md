# Third-party notices

GuitarBridge is licensed under the MIT license. Third-party components retain
their own copyright notices and license terms, included in `licenses/` and in
the portable EXE and MSI packages.

| Component | Version | License / notice |
| --- | --- | --- |
| NAudio | 2.2.1 | MIT, `licenses/NAudio.txt` |
| MaterialDesignThemes / MaterialDesignColors | 5.2.1 | MIT, `licenses/MaterialDesign.txt` |
| Microsoft.Xaml.Behaviors.Wpf | NuGet dependency | MIT, `licenses/XamlBehaviors.txt` |
| .NET runtime and Windows Desktop runtime | .NET 8, supplied by the SDK | `licenses/dotnet.txt`, `licenses/dotnet-third-party.txt` |

The build uses the WiX Toolset 5.0.2 to generate Windows Installer packages.
The WiX development tool is restored separately and is not shipped as an app
dependency. See <https://github.com/wixtoolset/wix/tree/v5.0.2> for its license.

Audio drivers, exported driver packages, SDK installations, private settings,
and device-specific diagnostics are not distributed with this project.
