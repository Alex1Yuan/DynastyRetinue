# Building from source

The public source tree is version **1.8.5**, including the optional guard protection switch **off by default**.

Requirements: Windows, a current .NET SDK, a locally installed copy of Rogue Trader, and Unity Mod Manager. Python 3.10+ is needed for the validation and packaging scripts. Game DLLs are referenced from your own installation; they are not distributed here.

Create the ignored file `src/DynastyRetinue/local.props` with your paths:

```xml
<Project>
  <PropertyGroup>
    <GameManaged>C:\Games\Rogue Trader\WH40KRT_Data\Managed</GameManaged>
    <UmmDir>C:\Users\YOUR_USER\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\UnityModManager</UmmDir>
  </PropertyGroup>
</Project>
```

From the repository root, run:

```powershell
py -3 tools/check_l10n.py
py -3 tools/check_names.py
dotnet build src/DynastyRetinue/DynastyRetinue.csproj -c Release
```

The project targets the game's .NET Framework/Mono runtime and deliberately uses its own BCL references. Do not replace them with unrelated framework DLLs. The output is `src/DynastyRetinue/bin/Release/DynastyRetinue.dll`.

To generate a release archive without deploying or touching your saves/settings:

```powershell
py -3 tools/pack_release.py 1.8.5
```

This creates `dist/DynastyRetinue-1.8.5.zip` from eight explicitly named files, excluding Settings.xml, logs, PDBs and test helpers. `bump.sh <version> pack` is the maintainer workflow: it validates data, updates the build manifest/version, builds, **deploys to UMM**, then packages. Use it only when deployment is intended; `DR_PYTHON` and `DR_DEPLOY` override its Python executable and target mod directory.

## Regression checks

```powershell
py -3 -m unittest discover -s tools/tests -p 'test_pack_release.py'
dotnet build tools/tests/GuardFriendlyFire/GuardFriendlyFire.csproj -c Release
& ./tools/tests/GuardFriendlyFire/bin/Release/net472/GuardFriendlyFire.exe
dotnet build tools/tests/GuardGrowth/GuardGrowth.csproj -c Release
& ./tools/tests/GuardGrowth/bin/Release/net472/GuardGrowth.exe
```

The two C# harnesses compile selected production classes with lightweight game doubles and reference Harmony/JSON from `GameManaged`. They check policy, damage-result isolation, toggle-off behavior, progression and persistence logic. They do not replace real-engine combat, cold-load or two-client co-op testing. Their top-level exception handler reports failures without invoking a JIT debugger.

The development workspace's decompiled game sources, local probes, saves, logs, binary dependencies, recordings and one-off in-game test helpers are excluded from the current public tree.
