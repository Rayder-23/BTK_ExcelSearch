# Plan: Windows installer for ExcelSearch

Version 1.0 · 14/10/2026 · Status: **DEFERRED** (approved in principle; waiting until the application changes settle)

## What

Replace "copy the `publish/` folder to each PC" with a normal Windows setup program (`ExcelSearch-Setup-<version>.exe`) built with
**Inno Setup**. The user runs it once and gets a Start Menu entry (optionally a desktop icon), an uninstaller, and clean upgrades.
Nobody has to open or see the folder full of DLLs.

## Why

- Today the app sits inside `publish/` among all its code files and the `.exe` is easy to miss. The goal is that normal users never see that folder.
- .NET offers no setting that tucks the DLLs of a framework-dependent app into subfolders and leaves only the `.exe` visible. Workarounds (a launcher exe, editing probing paths) are fragile or add another project to maintain. An installer solves the visibility problem and adds shortcuts, uninstall and upgrades on top.
- The existing decisions stay as they are: framework-dependent publish, not single-file, no trimming, ReadyToRun **not** enabled (see `Docs/Deployment.md`).

Options that were considered: a subfolder plus a small launcher exe; a subfolder plus a `.cmd` launcher; a plain zip with a README. The installer was chosen as the best fit for non-technical users.

## How (outline)

1. **Build:** `Scripts/publish.ps1` keeps producing the package, then calls the Inno Setup command-line compiler (`ISCC.exe`) on an installer script. The app version from the project file is passed in on the command line, so the setup file name and the installed version always match. The Inno Setup compiler is a one-time install on the build machine only.
2. **Install location:** per-user by default (no administrator rights needed), in the user's own Programs folder, so locked-down PCs can install it. Installing for all users under Program Files remains possible but asks for admin rights.
3. **Shortcuts and uninstall:** Start Menu entry, optional desktop icon, entry in "Apps & features".
4. **Upgrades:** every release uses the same fixed application ID, so a newer setup replaces the old files in place. The user's data is not touched: the SQLite file and the saved settings live under `%LocalAppData%\ExcelSearch`, outside the install folder, and the uninstaller leaves them alone unless we decide otherwise.
5. **Runtime check:** the setup checks that the .NET Desktop Runtime is present and, if not, tells the user and gives the download link (or offers to run the Microsoft installer).
6. **Docs:** `Deployment.md` and `AGENTS.md` get the new publish step; the installer output folder is git-ignored.

## Effort estimate

About half a day, mostly testing.

| Step | Work |
|---|---|
| Install Inno Setup 6 on the build machine (free) | 5 minutes, the only manual step |
| Write the installer script (name, version, folder, shortcuts, uninstaller, icon) | about 1 hour |
| Runtime check (warn and link) | about 30 minutes |
| Extend `publish.ps1` to build the installer with the right version | about 1 hour |
| Test: install, launch, upgrade over an old version, uninstall, run from a clean user account | 1-2 hours |
| Update the docs and `.gitignore` | 30 minutes |

## Things to know before starting

- **The setup file contains the `sa` password.** The installer carries `appsettings.Production.json`, so treat the setup `.exe` like the `publish/` folder: do not share it publicly and never commit it.
- **SmartScreen:** an unsigned installer makes Windows show "Windows protected your PC" and the user has to choose "Run anyway"; company policy can block it outright. Signing needs a paid certificate and still does not remove the warning immediately: reputation is per file, every new build starts at zero, and an EV certificate no longer gives instant reputation. Decision: do not sign for now; tell users to choose "Run anyway" (or avoid the issue by copying the setup over a share/USB inside the company).
- **Bundling the runtime** adds the Microsoft Desktop Runtime installer (about 56 MiB / 58.6 MB for 8.0.32) to the setup. Not needed if the PCs already have the runtime; the default plan only checks and links.
- **.NET 8 support ends on 10 November 2026** (.NET 10 is the next LTS, supported to 14 November 2028). The app already rolls forward to a newer installed runtime (`RollForward=LatestMajor`), but when the installer is built it is worth deciding whether to move the project to .NET 10 first and make the runtime check ask for that version. This is a separate decision, not part of the installer work itself.

## Verification of these details (web, 14/10/2026)

Confirmed:
- `PrivilegesRequired=lowest` makes the setup run without asking for elevation, even for administrators ([Inno Setup help](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm)). The help warns to use it only for installs that work without admin rights, which ours does (files in the install folder, data in `%LocalAppData%`).
- `{autopf}` becomes the user's own Program Files folder in non-administrative mode and the system Program Files in administrative mode ([constants](https://jrsoftware.org/ishelp/topic_consts.htm)). That per-user folder is normally `%LocalAppData%\Programs`; this was not verified on the build machine, so check it during testing.
- The application ID links an install to its uninstall records and is how a later setup is recognised as an update of the same product ([AppId](https://jrsoftware.org/ishelp/topic_setup_appid.htm)). It must stay the same for every release.
- The compiler accepts `/D` switches so a version can be passed in ([example](https://repo.mercurial-scm.org/hg/rev/baebf028f505)); to be confirmed against the Inno Setup help when the script is written.
- Unsigned installers show the SmartScreen warning; reputation is per file hash; EV certificates no longer give instant reputation ([Microsoft](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)).
- The official x64 Desktop Runtime 8 installer is 58,634,504 bytes (checked with an HTTP request to Microsoft's download link; 8.0.32). The "about 55 MB" given earlier in conversation was close.
- .NET 8 LTS ends 10 November 2026; .NET 10 LTS ends 14 November 2028 ([Microsoft support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)).

Not confirmed, to be tested when the work starts:
- The exact registry key for detecting the Desktop Runtime. A forum answer points at `HKLM\SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop` ([Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/985334/how-to-detect-whether-net-6-0-desktop-runtime-6-0)). It must be checked on a PC with the runtime and one without. A common community helper checks the base `Microsoft.NETCore.App` runtime, which is not enough for a WPF app.
- The `{userpf}` folder on the target Windows versions.

## When this is picked up

Start by re-checking the open points above, then the .NET 10 question, then follow the outline. Estimated 3-5 hours.
