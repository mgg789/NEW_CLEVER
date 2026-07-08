# Project Notes For Codex

## Communication
- Respond to the user in Russian unless they explicitly ask for another language.
- The repository contains Russian/Ukrainian localization files. Keep touched text files valid UTF-8.

## Build And Run
- Main app project: `Clever/Clever.csproj`, classic WPF/.NET Framework 4.8.
- SDK projects: `Interpreter/Interpreter.csproj` and `InterpreterConsole/InterpreterConsole.csproj`.
- The current machine may not have the system .NET Framework 4.8 targeting pack installed. If `C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8` is missing, use the local NuGet reference assemblies workflow below instead of assuming the normal solution build works.

### Local Build Workflow Used In This Workspace
From repository root:

```powershell
$ErrorActionPreference = 'Stop'
$refsVersion = '1.0.3'
$refsRoot = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.netframework.referenceassemblies.net48\$refsVersion"
if (!(Test-Path $refsRoot)) {
  $tmp = Join-Path $env:TEMP "microsoft.netframework.referenceassemblies.net48.$refsVersion.nupkg"
  Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/Microsoft.NETFramework.ReferenceAssemblies.net48/$refsVersion" -OutFile $tmp
  New-Item -ItemType Directory -Force -Path $refsRoot | Out-Null
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [System.IO.Compression.ZipFile]::ExtractToDirectory($tmp, $refsRoot)
}
$refs = Join-Path $refsRoot 'build\.NETFramework\v4.8'

dotnet msbuild Interpreter/Interpreter.csproj /t:Build /p:Configuration=Release
& 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe' 'Clever Registry/Clever Registry.csproj' /t:Build /p:Configuration=Release /p:Platform='AnyCPU' /p:FrameworkPathOverride="$refs" /p:TargetFrameworkDirectory="$refs"
```

BuildTools MSBuild cannot build `Clever.csproj` directly on this machine because its `ProjectReference` to the SDK-style `Interpreter.csproj` triggers SDK resolver issues. For manual testing, create a temporary `Clever/Clever.LocalBuild.csproj` beside `Clever.csproj`, remove the two `ProjectReference` items, and add this reference:

```xml
<ItemGroup>
  <Reference Include="Interpreter">
    <HintPath>..\Interpreter\bin\Release\netstandard2.0\Interpreter.dll</HintPath>
    <Private>True</Private>
  </Reference>
</ItemGroup>
```

Then build the temporary project into an isolated output folder:

```powershell
& 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe' Clever/Clever.LocalBuild.csproj /t:Build /p:Configuration=Release /p:Platform='AnyCPU' /p:FrameworkPathOverride="$refs" /p:TargetFrameworkDirectory="$refs" /p:OutputPath='bin\CodexRelease\' /p:IntermediateOutputPath='obj\CodexRelease\'
```

Copy runtime folders/files from `Clever/bin/Release` to `Clever/bin/CodexRelease` after this isolated build:

```powershell
$src = Resolve-Path 'Clever/bin/Release'
$dst = Resolve-Path 'Clever/bin/CodexRelease'
foreach ($dir in @('Localization','Examples','Help','Icons','Lib','Media')) {
  $from = Join-Path $src $dir
  if (Test-Path $from) { Copy-Item -LiteralPath $from -Destination $dst -Recurse -Force }
}
foreach ($file in @('Clever Registry.exe','Clever Registry.exe.config','Clever Registry.pdb','cleverIcon.ico')) {
  $from = Join-Path $src $file
  if (Test-Path $from) { Copy-Item -LiteralPath $from -Destination $dst -Force }
}
```

Run the updated app:

```powershell
Start-Process -FilePath (Resolve-Path 'Clever/bin/CodexRelease/Clever.exe') -WorkingDirectory (Resolve-Path 'Clever/bin/CodexRelease')
```

## Runtime Nuances
- CLEV3R is single-instance by process name and also opens a named pipe `clever_pipe`. Do not try to run a second instance side by side. Close the old `Clever.exe` first, otherwise the new instance will exit or fail during startup.
- If `Clever/bin/Release/Clever.exe` is running, rebuilding into `bin/Release` fails because the executable is locked. Use `bin/CodexRelease` for manual test builds or close the running app before building into `bin/Release`.
- `Clever/bin/CodexRelease/` and `Clever/Clever.LocalBuild*.csproj` are local build artifacts and should not be committed.

## Git Feature Notes
- Local Git functionality intentionally uses installed `git.exe`.
- Remote/cloud buttons are still stubs in this stage and must not run `git pull`, `git push`, or remote setup commands.
- Git status is based on saved files on disk. After `Ctrl+S`, Save All, Save As, or the save step before EV3 compile, the Git panel should refresh itself; Git actions also force a fresh `git status` before commit/message generation.
- Save-triggered Git refresh is intentionally debounced and status-only; full history refresh is reserved for init, reload, commit, restore, and branch switch.
- Git diff previews render parsed display lines in a read-only Scintilla tab. Keep Git metadata lines visually separate from added/deleted content instead of dumping raw patch text directly.
- Diff preview files are Scintilla fold headers and should open collapsed. Use hunk coordinates to insert compact hidden-context placeholders instead of expanding full unchanged file ranges.
- After local Git operations that change files on disk, call `MainWindowVM.ReloadOpenProjectFilesFromDisk(repositoryRoot)` so already-open `.bp/.bpi/.bpm` tabs update without reopening.
- The commit-history rollback button restores the repository files to the selected commit snapshot with `git restore --source <hash> --staged --worktree :/`, then leaves the rollback as local changes for the user to review and commit. It intentionally does not use `git revert`, because inverse patches can conflict after later edits to the same files.
- EV3 compilation adds a `.gitignore` block for `~*/` only when the compiled file is already under an initialized Git repository.
