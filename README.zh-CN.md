# DSH Deploy — DeepSeek Harness 一键部署器

[English](README.md) | **简体中文**

一个单文件 Windows 程序，把一台干净的机器从零带到可用的 DeepSeek Harness 网页界面：
检测前置环境 → 按需安装缺失组件 → 安装插件市场 → 询问 DeepSeek API Key → 启动 DSH。

产物：`dist/DSH-Deploy.exe`（约 350 KB，无需 .NET SDK、无需 NuGet、无需任何第三方运行时）。

## 程序做什么

1. **检测前置环境**：Node.js、pnpm、Git、dsh。逐个报告“已安装 / 未安装 / 版本过低”，并显示解析到的实际路径。
2. **征求同意后安装**：缺失或版本过低的组件会逐个弹窗询问；任一环节选择“否”即退出（退出码 0），不会留下半成品。
3. **安装插件市场**：`dsh plugin --profile web add dshmarket`，实现 `dshmarket` 插件市场。
4. **首次运行询问 API Key**：可直接写入 DSH 凭据文件；选择“否”也会提示稍后可在 DSH 页面设置。
5. **启动 DSH**：以分离进程运行 `dsh web --port <端口>`，等待端口就绪后由 DSH 自己打开浏览器。

## 使用

双击 `dist/DSH-Deploy.exe`（清单已声明 `requireAdministrator`，会弹 UAC）。

### 命令行

```
DSH-Deploy.exe [选项]

  --region cn|global   强制使用国内/官方源（覆盖 IP 检测）
  --port <n>            DSH Web 端口（默认 3080）
  --registry <url>      npm registry 覆盖
  --dsh-home <path>     DSH_HOME 覆盖（默认 %USERPROFILE%\.dsh）
  --headless            无界面模式（必须同时给 --yes）
  --yes                 对所有安装询问自动同意
  --no-launch           完成部署但不启动 DSH
  --no-market           跳过插件市场安装
  --no-api-key          跳过 API Key 询问
  --self-test           运行内置自检并退出（0 通过 / 1 失败）
  -V, --version         显示版本
```

只做只读检查、不修改机器的例子：

```
DSH-Deploy.exe --headless --yes --no-market --no-api-key --no-launch
```

## 源的选择（实测，不看地区）

流程是：

1. 先检测前置环境（和插件市场是否已安装）。
2. 只有确实需要下载时，才对每个候选源取样实测：取真实小文件（连取 2 次、取中位数、单次上限 512 KB），得到「首字节延迟 + 实测吞吐」。
3. 按组分头排序——npm registry / Node 镜像 / Git 镜像 各自独立，所以一个源可以在这个组最快、在那组落后。
4. 排序规则是**吞吐为主（权重 6）、延迟为辅（权重 1）**，得分越低越好。IP 归属只用来打破 5% 以内的平局，并且**永远不会**让一个实测明显更慢的源排到第一。

`--region cn|global` 或 `DSH_DEPLOY_REGION` 会**直接跳过测速**，使用固定的国内/官方顺序。

| 组件 | 候选源 |
|---|---|
| npm / pnpm / dsh | `registry.npmmirror.com`、`registry.npmjs.org` |
| Node.js MSI / ZIP | `registry.npmmirror.com/-/binary/node`、`nodejs.org/dist` |
| Git for Windows | `npmmirror.com` 镜像、GitHub Releases、清华 TUNA |
| pnpm 独立版 | GitHub Releases（`pnpm-win32-x64.zip`） |

每个组的所有候选源都会保留在结果里，所以一次测速失准只会让它排在后面，不会让下载失败。所有下载地址强制 HTTPS，Node 安装包还会用官方 `SHASUMS256.txt` 校验 SHA-256。实际使用的顺序、每项测速数值都会写入日志。

> 注：清华 TUNA 只作为 Git 的**备选**保留，不参与测速（它的发布目录对个别文件名会返回 404，作为测速样本不可靠）。

## 版本策略（重要）

### dsh 版本：钉在插件生态支持的那一版

**默认安装 `0.1.6-alpha.2`**，并且刻意避开两个标签：

| 标签 / 版本 | 为什么不用 |
|---|---|
| `latest` → `0.1.5-rc.2` | 比当前版本**更旧** |
| `alpha` → `0.1.7-alpha.1` | **更新，但插件生态还不支持**：现存插件的 peer 范围里完全不出现 0.1.7，多数只写到 `0.1.6-alpha.1` / `0.1.6-alpha.2` |
| **`0.1.6-alpha.2`（默认）** | 可正常安装的具体版本，且有插件明确声明支持 |

实测依据（本机一份可正常运行的 profile，10 个插件）——各插件声明的核心版本：

| 插件 | 声明支持的 dsh 核心版本 |
|---|---|
| `@michengai/dsh-automation` | …`0.1.6-alpha.1`, **`0.1.6-alpha.2`** |
| `@nanmicoder/dsh-agent-teams` | 最高 `0.1.5-rc.1` |
| `dsh-better-sidebar` | `^0.1.5-rc.1` |
| `dsh-damage-pulse` | 最高 `0.1.6-alpha.1` |
| `dsh-dream-skin` / `dsh-email` | `^0.1.0-rc.6` |
| `dsh-office-tools` | 最高 `0.1.5-alpha.0` |
| `dshmarket` 1.52.0 | `@deepseek-ai/dsh-settings: ^0.1.0-rc.7 \|\| ^0.1.1-rc.2 \|\| ^0.1.2-alpha.2` |

注意最后一行：连插件市场自己的 peer 范围都还停留在 `0.1.2-alpha.2`。**整个生态的 peer 声明普遍滞后**，所以 pnpm 不会因 peer 不匹配而拒绝安装，真正的兼容性只在运行时才暴露——这正是「按 peer 范围自动选版本」不可行的原因，也是这里改用**实际验证过的版本**的原因。

两处相关行为：

- **绝不降级**：本机已装的 dsh 若更新，部署器保持不动（别人的安装不该被我们悄悄改掉）。
- **但要明确警告**：若已装版本比生态支持的版本更新，会打印并可回退提示：
  `npm i -g @deepseek-ai/dsh@0.1.6-alpha.2`

覆盖方式：`DSH_DEPLOY_DSH_VERSION=0.1.6-alpha.2`。

- **Node.js 最低 22.19.0**（DSH 依赖树中的 `libreoffice-kit` 要求 `>=22.19.0`），默认安装 **22.20.0 LTS**。覆盖方式：`DSH_DEPLOY_NODE_VERSION`。

## API Key 处理

DSH 从 `%USERPROFILE%\.dsh\.credentials.yaml` 读取凭据。该文件是 `version: 1` 文档，含 `refs:`（环境变量名 → 值）与 `records:`（各插件的凭据记录）。

- 若启动环境里已有 `DEEPSEEK_API_KEY`，优先级最高，程序只报告并跳过，绝不覆盖。
- 若文件中已有该 Key，跳过询问。
- 否则弹窗询问；选择“否”时提示可稍后在 DSH 页面设置。
- 选择“是”时：校验 Key 形态（`sk-` 前缀、无空格换行），然后**文本级合并**——已存在则原地替换，不存在则插入到既有 `refs:` 段内；`records:`、注释与格式**原样保留**。写入使用临时文件 + 原子替换，写入后回读校验。
- 若文件结构异常（未知顶层键、重复键等），**放弃写入**并提示改用 DSH 页面添加——因为 DSH 遇到这类文件会拒绝启动，绝不能由部署器制造它的坏状态。
- 日志中所有 `sk-…` 一律打码为 `sk-****`，该处理在日志写入的最底层执行。

## 构建

```
powershell -ExecutionPolicy Bypass -File build\Build.ps1          # 或加 -Clean
```

## 已知限制

- 仅支持 Windows x64（检测到 ARM64 会明确报告不支持）。
- 需要 .NET Framework 4.8（Windows 10/11 自带）。
- 若本机安全软件拦截 MSI/EXE 静默安装，安装会失败并给出退出码与 msiexec 日志路径；此时可手动安装后重跑本程序。
- 插件市场安装失败不会阻断 DSH 启动，程序会提示可在 DSH 页面重试。
- 端口已被占用时视为“DSH 正在运行”并直接打开，不会启动第二个实例。
- `--no-launch` 下不会打开任何浏览器（即使 DSH 已在运行）。

## 目录结构

```
src/DSHDeploy/
  Program.cs                入口：wizard / --headless / --self-test 调度
  app.manifest              requireAdministrator、DPI 感知
  Core/Model.cs             选项、源计划模型、SemVer 比较（含预发布规则）
  Core/Sources.cs           候选源地址、探测目标与备选顺序
  Core/Region.cs            源测速与排序（延迟 + 吞吐；IP 仅用于打破平局）
  Core/EnvChecker.cs        前置环境检测与“是否需要安装”决策
  Core/Downloader.cs        多镜像重试下载与 SHA-256 校验
  Core/Proc.cs              受限子进程执行与可执行文件绝对路径解析
  Core/PathSync.cs          注册表/进程 PATH 合并且无需重启
  Core/NodeInstaller.cs     Node.js MSI，失败回退便携版 ZIP
  Core/GitInstaller.cs      Git for Windows 静默安装
  Core/PnpmInstaller.cs     npm → corepack → 独立版三级回退
  Core/DshInstaller.cs      dsh 安装与版本比较
  Core/MarketInstaller.cs   插件市场安装与 profile 清单校验
  Core/CredentialsWriter.cs 凭据文件校验与安全合并
  Core/Launcher.cs          启动 dsh web、等待端口、打开浏览器
  Core/SelfTest.cs          内置自检
  Ui/MainForm.cs            向导界面
assets/deepseek-bowl.ico    程序图标（SHA-256 db20bea4…）
build/Build.ps1             构建脚本
```
## 许可

启动器源码可按 MIT 使用。  
鲸鱼娘立绘按原作者许可使用（社区二创，多为非商业）。  
DeepSeek Harness 本身为上游 MIT 项目，版权归 DeepSeek AI。
