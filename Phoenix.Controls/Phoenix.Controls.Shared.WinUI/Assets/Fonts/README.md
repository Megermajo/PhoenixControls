# Phoenix Controls — Packaged Fonts

This folder ships the three custom font families the Phoenix Controls suite binds
through `PhoenixDark.xaml` (`DisplayFont`, `SansFont`, `MonoFont`).

The XAML FontFamily entries in [`../../Themes/PhoenixDark.xaml`](../../Themes/PhoenixDark.xaml)
use the `ms-appx:///Assets/Fonts/<filename>.ttf#<Family Name>` syntax with the
existing system-font fallback chain preserved as commas behind it. That means:

- When the `.ttf` files are present here, WinUI resolves them.
- When they're missing, WinUI silently walks the fallback chain
  (Trajan Pro / Cinzel / Georgia for `DisplayFont`,
  Segoe UI Variable / Segoe UI for `SansFont`,
  Cascadia Code / Consolas for `MonoFont`).

This makes the foundation tokens drop-in: once the canonical TTFs land here,
the brand fonts light up across the suite with zero XAML changes.

## Expected TTF filenames

The csproj globs `Assets/Fonts/**` into `Content` with `CopyToOutputDirectory =
PreserveNewest`, so any TTF you drop here ships into each pillar's bin output
alongside the suite's existing assets.

| Family                | Filename                                  | Source                                                                                              | License             |
|-----------------------|-------------------------------------------|-----------------------------------------------------------------------------------------------------|---------------------|
| Cormorant Garamond    | `CormorantGaramond-Regular.ttf`           | [Google Fonts — Cormorant Garamond](https://fonts.google.com/specimen/Cormorant+Garamond)           | SIL OFL 1.1         |
|                       | `CormorantGaramond-SemiBold.ttf`          | (same)                                                                                              | SIL OFL 1.1         |
| Inter                 | `Inter-Regular.ttf`                       | [rsms.me/inter](https://rsms.me/inter/) · [Google Fonts](https://fonts.google.com/specimen/Inter)   | SIL OFL 1.1         |
|                       | `Inter-SemiBold.ttf`                      | (same)                                                                                              | SIL OFL 1.1         |
| JetBrains Mono        | `JetBrainsMono-Regular.ttf`               | [JetBrains Mono download](https://www.jetbrains.com/lp/mono/)                                       | SIL OFL 1.1         |

The SIL Open Font License (OFL) 1.1 requires the license text to ship alongside
each font file. Drop the upstream `OFL.txt` from each font's source bundle into
this folder when adding the TTFs:

- `OFL-CormorantGaramond.txt`
- `OFL-Inter.txt`
- `OFL-JetBrainsMono.txt`

(Three separate files because each upstream bundle has its own license header
with author/copyright attribution that the OFL requires preserving verbatim.)

## How the lookup resolves

PhoenixDark.xaml stores the family entries as:

```xml
<x:String x:Key="DisplayFont">
    ms-appx:///Assets/Fonts/CormorantGaramond-Regular.ttf#Cormorant Garamond,
    Cormorant Garamond, Trajan Pro, Cinzel, Georgia, serif
</x:String>
```

WinUI parses the comma-separated chain and picks the first family it can
resolve. The first entry uses the `ms-appx:` packaged-asset URI; the rest are
system-font fallbacks. The fallback chain is identical to the pre-0.10.0
chain so removing the TTFs (or never adding them) produces the same visual
output the suite had before this scaffold was added.

## When you add the TTFs

1. Copy the canonical `.ttf` files in (filenames above — case-sensitive on
   case-sensitive filesystems even though Windows is forgiving).
2. Copy the matching `OFL-*.txt` license file in for each family.
3. `dotnet build Phoenix.Controls/Phoenix.Controls.sln -c Debug` — the
   csproj glob picks them up automatically; no XAML or csproj edits needed.
4. Launch Hub.WinUI and confirm the Welcome dialog / panel headers / mono
   value pills render in the packaged families. Architect / Visualist
   inherit through the same App.xaml merged dictionary.
