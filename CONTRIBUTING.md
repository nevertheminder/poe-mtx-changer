# Contributing

Issues and pull requests are welcome.

## Reporting problems

Use the issue forms. Include the app version and, for the wardrobe, a screenshot of the same item in the wardrobe and
in the game. Never attach game files, Oodle or FMOD files, or personal data. Security problems go privately, see
[SECURITY.md](SECURITY.md).

## Building

You need Windows 10/11 x64, the .NET 10 SDK, and Visual Studio Build Tools with the C++ x64 tools (for the 3D
renderer, built together with the solution).

```bash
dotnet build PoeMtxChanger.sln -c Release
dotnet test tests/PoeMtxChanger.Core.Tests
```

Run the app with `--demo` to see a sample catalog without a game installation. Tests that need the game skip
themselves when it is not installed.

| Folder | What is in it |
|---|---|
| `src/PoeMtxChanger.Core` | Game file formats, catalog, applying and restoring, profiles |
| `src/PoeMtxChanger.App` | The Windows app and the 3D wardrobe |
| `native/preview` | The Direct3D 11 renderer |
| `tests/` | Unit tests |
| `tools/` | Developer checks that read a local game installation |

## Pull requests

- One change per pull request, branched from `main`. The build and tests run automatically and must pass.
- Everything is in English: interface, messages, code and comments.
- No game files, Oodle or FMOD files, other copyrighted assets, or personal data (such as paths on your computer).
- Do not change the installed game while developing; work on copies.
- Keep each file's line endings, and add user-visible changes to `CHANGELOG.md`.

Out of scope: anything that gives a gameplay advantage, unlocks purchases, shows swapped cosmetics to other players, or
touches the running game, its memory or network traffic.

By sending a pull request you agree that your change may be used and shared as part of this project (the project has
no open-source licence; all rights are reserved by the author).
