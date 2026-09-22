# DSH Deploy — One-Click DeepSeek Harness Installer

**English** | [简体中文](README.zh-CN.md)

A single-file Windows program that takes a clean machine from zero to a working DeepSeek Harness web UI:

detect prerequisites → install missing components on demand → install the plugin marketplace → ask for a DeepSeek API key → start DSH.

Artifact: `dist/DSH-Deploy.exe` (~350 KB; no .NET SDK, no NuGet, no third-party runtime required).

## What it does

1. **Detects prerequisites**: Node.js, pnpm, Git, and dsh. Reports each as installed / missing / too old, and shows the resolved path.
2. **Installs only after consent**: Missing or outdated components are confirmed one by one in dialogs. Choosing “No” at any step exits (code 0) and leaves no half-finished install.
3. **Installs the plugin marketplace**: `dsh plugin --profile web add dshmarket` to enable the `dshmarket` marketplace.
4. **Asks for an API key on first run**: Can write it into the DSH credentials file. Choosing “No” still tells you that you can set it later on the DSH page.
5. **Starts DSH**: Runs `dsh web --port <port>` as a detached process, waits until the port is ready, then lets DSH open the browser itself.

## Usage

Double-click `dist/DSH-Deploy.exe` (the manifest declares `requireAdministrator`, so UAC will appear).

### Command line

```
DSH-Deploy.exe [options]

  --region cn|global   Force China / official mirrors (overrides IP detection)
  --port <n>            DSH web port (default 3080)
  --registry <url>      Override npm registry
  --dsh-home <path>     Override DSH_HOME (default %USERPROFILE%\.dsh)
  --headless            No UI (must also pass --yes)
  --yes                 Auto-accept every install prompt
  --no-launch           Finish deploy but do not start DSH
  --no-market           Skip plugin marketplace install
  --no-api-key          Skip API key prompt
  --self-test           Run built-in self-test and exit (0 pass / 1 fail)
  -V, --version         Print version
```

Read-only check that does not change the machine:

```
DSH-Deploy.exe --headless --yes --no-market --no-api-key --no-launch
```

## Mirror selection (measured speed, not geography)

Flow:

1. First detect prerequisites (and whether the marketplace is already installed).
2. Only if a download is actually needed, probe each candidate: fetch a real small file twice, take the median, cap each fetch at 512 KB, and record first-byte latency plus measured throughput.
3. Rank independently per group — npm registry / Node mirrors / Git mirrors are separate, so a source can win one group and lose another.
4. Ranking is **throughput-first (weight 6), latency-second (weight 1)**; lower score is better. IP geo is used only to break ties within 5%, and **never** promotes a source that measured clearly slower.

`--region cn|global` or `DSH_DEPLOY_REGION` **skips probing** and uses a fixed China / official order.

| Component | Candidates |
|---|---|
| npm / pnpm / dsh | `registry.npmmirror.com`, `registry.npmjs.org` |
| Node.js MSI / ZIP | `registry.npmmirror.com/-/binary/node`, `nodejs.org/dist` |
| Git for Windows | `npmmirror.com` mirror, GitHub Releases, Tsinghua TUNA |
| Standalone pnpm | GitHub Releases (`pnpm-win32-x64.zip`) |

Every candidate in a group stays in the result list, so one bad probe only demotes a source instead of failing the download. All download URLs are forced to HTTPS. Node packages are also verified against the official `SHASUMS256.txt` SHA-256. The order actually used and every probe number are written to the log.

> Note: Tsinghua TUNA is kept only as a **Git fallback** and is not probed (its release directory 404s on some filenames, so it is unreliable as a probe sample).

## Version policy (important)

### dsh version: pin to what the plugin ecosystem actually supports

**Default install is `0.1.6-alpha.2`**, and two tags are deliberately avoided:

| Tag / version | Why not |
|---|---|
| `latest` → `0.1.5-rc.2` | **Older** than the current pin |
| `alpha` → `0.1.7-alpha.1` | **Newer, but the plugin ecosystem does not support it yet**: no existing plugin peer range mentions 0.1.7; most stop at `0.1.6-alpha.1` / `0.1.6-alpha.2` |
| **`0.1.6-alpha.2` (default)** | A concrete version that installs cleanly and is explicitly declared by plugins |

Evidence from a working local profile (10 plugins) — declared core versions:

| Plugin | Declared dsh core versions |
|---|---|
| `@michengai/dsh-automation` | …`0.1.6-alpha.1`, **`0.1.6-alpha.2`** |
| `@nanmicoder/dsh-agent-teams` | up to `0.1.5-rc.1` |
| `dsh-better-sidebar` | `^0.1.5-rc.1` |
| `dsh-damage-pulse` | up to `0.1.6-alpha.1` |
| `dsh-dream-skin` / `dsh-email` | `^0.1.0-rc.6` |
| `dsh-office-tools` | up to `0.1.5-alpha.0` |
| `dshmarket` 1.52.0 | `@deepseek-ai/dsh-settings: ^0.1.0-rc.7 \|\| ^0.1.1-rc.2 \|\| ^0.1.2-alpha.2` |

Note the last row: even the marketplace’s own peer range still stops at `0.1.2-alpha.2`. **Peer declarations across the ecosystem lag behind**, so pnpm will not refuse the install for peer mismatch — real compatibility only shows up at runtime. That is why auto-picking by peer range is not viable, and why this project pins a **version that has been verified in practice**.

Two related behaviors:

- **Never downgrade**: if the machine already has a newer dsh, the deployer leaves it alone (someone else’s install should not be silently changed).
- **Warn clearly**: if the installed version is newer than what the ecosystem supports, print a rollback hint:
  `npm i -g @deepseek-ai/dsh@0.1.6-alpha.2`

Override: `DSH_DEPLOY_DSH_VERSION=0.1.6-alpha.2`.

- **Node.js minimum 22.19.0** (`libreoffice-kit` in the DSH tree requires `>=22.19.0`); default install is **22.20.0 LTS**. Override: `DSH_DEPLOY_NODE_VERSION`.

## API key handling

DSH reads credentials from `%USERPROFILE%\.dsh\.credentials.yaml`. That file is a `version: 1` document with `refs:` (env var name → value) and `records:` (per-plugin credential records).

- If `DEEPSEEK_API_KEY` is already in the process environment, it wins: the program reports it and skips, never overwriting.
- If the file already contains the key, skip the prompt.
- Otherwise show a dialog; choosing “No” tells you that you can set it later on the DSH page.
- Choosing “Yes”: validate the key shape (`sk-` prefix, no spaces or newlines), then **merge at text level** — replace in place if present, otherwise insert into the existing `refs:` block; `records:`, comments, and formatting are **left intact**. Write via temp file + atomic replace, then read back to verify.
- If the file structure is abnormal (unknown top-level keys, duplicate keys, etc.), **do not write** and tell the user to add the key on the DSH page — DSH refuses to start on such files, and the deployer must not create that bad state.
- Every `sk-…` in logs is masked as `sk-****` at the lowest logging layer.

## Build

```
powershell -ExecutionPolicy Bypass -File build\Build.ps1          # or add -Clean
```

## Known limitations

- Windows x64 only (ARM64 is detected and reported as unsupported).
- Requires .NET Framework 4.8 (included with Windows 10/11).
- If local security software blocks silent MSI/EXE installs, the install fails with an exit code and msiexec log path; install manually and re-run this program.
- A marketplace install failure does not block starting DSH; the program tells you to retry from the DSH page.
- If the port is already in use, treat DSH as running and open it; do not start a second instance.
- With `--no-launch`, no browser is opened (even if DSH is already running).

## Layout

```
src/DSHDeploy/
  Program.cs                Entry: wizard / --headless / --self-test dispatch
  app.manifest              requireAdministrator, DPI awareness
  Core/Model.cs             Options, source-plan model, SemVer compare (incl. prerelease)
  Core/Sources.cs           Candidate URLs, probe targets, fallback order
  Core/Region.cs            Mirror probing and ranking (latency + throughput; IP only for ties)
  Core/EnvChecker.cs        Prerequisite detection and “need install?” decisions
  Core/Downloader.cs        Multi-mirror retry download and SHA-256 verify
  Core/Proc.cs              Restricted child-process exec and absolute path resolve
  Core/PathSync.cs          Merge registry/process PATH without reboot
  Core/NodeInstaller.cs     Node.js MSI, fallback to portable ZIP
  Core/GitInstaller.cs      Silent Git for Windows install
  Core/PnpmInstaller.cs     npm → corepack → standalone three-level fallback
  Core/DshInstaller.cs      dsh install and version compare
  Core/MarketInstaller.cs   Marketplace install and profile manifest check
  Core/CredentialsWriter.cs Credentials file validate and safe merge
  Core/Launcher.cs          Start dsh web, wait for port, open browser
  Core/SelfTest.cs          Built-in self-test
  Ui/MainForm.cs            Wizard UI
assets/deepseek-bowl.ico    App icon (SHA-256 db20bea4…)
build/Build.ps1             Build script
```
