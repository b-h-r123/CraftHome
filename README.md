# CraftHome

面向 Minecraft 新手服主的 C# Web 控制台，基于成熟的 MCSManager 管理引擎。当前版本 **v0.2 新手开服向导**。

## 已实现

- 单个服主登录：ASP.NET Core Identity、SQLite、随机首次密码、失败登录锁定。
- 关闭公众注册；管理页面要求 Owner 权限。
- 浏览器配置一个已有 MCSManager 实例，API Key 不放在 URL 和浏览器持久存储中。
- 查询实例状态、进程内存、游戏查询响应、最近 16 KB 日志。
- 启动、正常停止、重启；停止和重启需要确认。
- 有清晰标注的演示模式，不会假装连接了真实服务器。
- 适配手机布局。
- Linux x64 自包含发行、systemd 安装脚本。
- Ubuntu 24.04 x86_64 安装器：准备 Java 21 和官方 MCSManager，自动连接管理引擎。
- 新手向导：环境检测、Paper 1.21.x 稳定构建选择、SHA-256 校验、独立实例创建和首次启动。
- 可选择正版验证或离线账号白名单；显式确认 EULA 后才写入接受状态。
- 已有实例切换，创建过程保留旧实例和世界。

当前不包含已有世界原地换版本、备份恢复、Mod／插件市场、游戏内密码、角色或领地管理。Windows 可以运行控制台并管理已有实例；自动创建目前限定 Linux 同机部署。

## 新手快速开始

准备一台 **Ubuntu 24.04 x86_64** 服务器，建议至少 2 GiB 内存和 2 GiB 可用磁盘；多人游玩需要更多内存。具备 sudo 权限，并能连接 GitHub、Node.js、Paper 和 Minecraft 官方下载源。

```bash
curl -fL https://raw.githubusercontent.com/b-h-r123/CraftHome/main/deploy/linux/bootstrap.sh -o bootstrap.sh
sudo bash bootstrap.sh --public
```

安装器从本仓库最新 Release 下载自包含程序并核对 SHA-256。`--public` 使 WebUI 监听 `0.0.0.0:5080`；不加此参数时使用 SSH 隧道访问。云安全组需要另行允许 TCP 5080 和游戏端口（默认 25565）。MCSManager 的管理端口仍只监听本机。

1. 打开 `http://服务器公网IP:5080`，用 `sudo cat /var/lib/crafthome/first-login.txt` 中的账号登录。
2. 点击“新手开服”，检查 Java、内存、磁盘和端口。
3. 选择 Paper 版本，填写内存、公网 IP 和游戏端口，阅读并确认 EULA。
4. 点击“创建服务器”，完成后点击“首次启动并管理”。
5. 等待“查询已响应”，再用相同版本的 Minecraft Java 客户端连接 `公网IP:游戏端口`。

控制台查询成功只说明服务器本机游戏服务已响应；公网进入还受防火墙、安全组和客户端版本影响。当前公网入口使用 HTTP，长期部署应配置 HTTPS。

升级已有 CraftHome：`sudo bash bootstrap.sh --upgrade`；先只检测：`sudo bash bootstrap.sh --check`。完整操作、源码构建和故障排查见 [v0.2 开服指南](docs/v0.2-开服指南.md)。

## Windows 本地启动

安装 [.NET 10 SDK](https://learn.microsoft.com/en-us/dotnet/core/install/windows)，然后取得源码并启动：

```powershell
git clone https://github.com/b-h-r123/CraftHome.git
cd CraftHome
.\start.ps1
```

浏览器打开 `http://127.0.0.1:5080`。

当前开发电脑的源码位于 `D:\MCstart`，可以直接在该目录执行 `start.ps1`。该电脑使用的私有 SDK `.local/dotnet` 不随 Git 仓库上传，也不会改变系统全局 SDK。

首次启动会在 `src/CraftHome.Web/.local/first-login.txt` 生成登录信息，邮箱为 `owner@crafthome.local`。读取此文件登录；它不会出现在网页或控制台日志中。登录后可以修改密码。

```powershell
Get-Content .\src\CraftHome.Web\.local\first-login.txt
```

Windows 本地开发默认进入演示模式。在“连接设置”选择真实连接并填写：

1. MCSManager 面板地址，通常是 `http://127.0.0.1:23333`。
2. 在原面板生成的 API Key，需启用 API Key 功能并具有该实例权限。
3. 节点 ID `daemonId`。
4. 实例 ID `uuid`，可从原面板实例终端页面 URL 获取。
5. 展示给玩家的连接地址。

保存后返回服务器页面刷新。此版本中“进程运行”与“游戏查询响应”分开显示，两者都不能证明公网可达。

### 从其他电脑取得源码后

安装 [.NET 10 SDK](https://learn.microsoft.com/en-us/dotnet/core/install/windows)；然后运行 `start.ps1`。不需要复制当前 `.local` 目录的运行数据或密钥。

## Linux 部署

在有 SDK 的开发电脑生成自包含发行包（Linux 服务器不需要 .NET SDK）：

```powershell
dotnet publish src/CraftHome.Web/CraftHome.Web.csproj -c Release -r linux-x64 --self-contained true -o artifacts/linux-x64
tar -czf artifacts/crafthome-linux-x64.tar.gz -C artifacts/linux-x64 .
```

当前开发电脑可将命令中的 `dotnet` 替换为 `.\.local\dotnet\dotnet.exe`。把发行包和 `deploy/linux/bootstrap.sh` 上传到 Ubuntu 24.04 x86_64：

```bash
sudo bash bootstrap.sh /绝对路径/crafthome-linux-x64.tar.gz
systemctl status crafthome --no-pager
```

已有安装需明确添加 `--upgrade`，程序会先备份，账号和游戏数据保留。控制台默认只监听本机 `127.0.0.1:5080`。从本地电脑开 SSH 隧道：

```powershell
.\scripts\connect-server.ps1 -Server 服务器IP -User SSH用户名
```

然后打开 `http://127.0.0.1:5081`。SSH 密码在 SSH 提示中输入；脚本不保存密码。

首次登录信息位于服务器 `/var/lib/crafthome/first-login.txt`。远程控制台的 MCSManager 地址填服务器自己能访问的地址；同机部署通常仍是 `http://127.0.0.1:23333`。

MCSManager 采用官方发行版。当前验证目标为 Web 10.22.0／Daemon 4.19.3，Node.js 22.23.4。安装和初始化参考 [官方仓库](https://github.com/MCSManager/MCSManager)。其管理端口应按部署拓扑限制访问，测试环境采用回环地址和 SSH 隧道。

安装器使用固定版本的官方 MCSManager 发行文件并核对哈希，没有修改上游核心，也不自动接受 Minecraft EULA。保留的底层 `install.sh` 只负责安装控制台，通常应使用 `bootstrap.sh`。

### 使用公网 IP 和端口访问

已安装的控制台可以通过以下脚本切换到公网 HTTP 入口：

```bash
sudo bash deploy/linux/enable-public-access.sh
```

脚本仅修改 CraftHome 的 systemd 启动配置，使其监听 `0.0.0.0:5080`，并在存在 UFW 时添加 TCP 5080 允许规则。MCSManager 的管理端口仍保留在回环地址。云服务器还需要在云平台安全组中允许 TCP 5080；系统防火墙规则不能替代云安全组。

然后访问 `http://服务器公网IP:5080`，使用原 CraftHome 账户重新登录。此方式不依赖 SSH 隧道。当前入口是 HTTP，长期公网部署应补充 HTTPS。

本次测试机的实际配置和 SSH 凭据区别见 [公网访问说明](docs/公网访问说明.md)。

## 数据与密钥

- 本地开发数据：`src/CraftHome.Web/.local/`。
- Linux 安装数据：`/var/lib/crafthome/`。
- 自定义目录：环境变量 `CraftHome__DataDirectory`。
- `crafthome.db`：服主账户和 Identity 数据。
- `connection.json`：实例连接配置，API Key 使用 Data Protection 保护。
- `keys/`：Data Protection 密钥，与配置共同备份并限制文件访问。
- `first-login.txt`：只在首次创建账户时生成，建议修改密码后删除。

`.local` 不进入 Git，也不进入发行包。仅丢弃数据库或 keys 中的一部分，可能导致无法登录或无法解密连接配置。

## 测试

先停止正在运行的本地开发实例，再执行：

```powershell
dotnet test tests/CraftHome.Tests/CraftHome.Tests.csproj
```

自动测试覆盖访问控制、防伪请求、关闭注册、API 错误、有限日志、密钥不出现在 URL、EULA、下载校验、离线白名单、资源不足不创建实例和文件上传等。完整浏览器测试入口为 `tests/browser/smoke.cjs`，仅对隔离测试环境执行；其会创建并启动一个真实实例。测试方法见 [v0.2 开服指南](docs/v0.2-开服指南.md)。

浏览器与 Linux 实服测试结果见 `docs/测试报告.md`；只有报告标记通过的项目才算已验证。

v0.2 的新增验证见 [v0.2 测试报告](docs/v0.2-测试报告.md)，自动化工作流见 [Actions](https://github.com/b-h-r123/CraftHome/actions)。

最新公网游戏连接、PCL 离线账号和白名单验证见 [玩家连接与实服验证](docs/玩家连接与实服验证.md)。WebUI 中“玩家连接地址”用于展示游戏入口；修改该文字不会自动修改游戏实际监听配置。

## 项目结构

```text
src/CraftHome.Web/
  Components/Pages/          服务器、连接设置与新手创建向导
  Components/Account/        基于微软 Identity 模板的账户流程
  Services/McsManagerClient.cs  固定 API 适配
  Services/ServerService.cs     实例控制与显式演示模式
  Services/ConnectionStore.cs   受保护连接配置
  Services/ServerProvisioner.cs 创建与首次启动流程
  Services/PaperCatalog.cs      官方版本与哈希校验
  Services/HostInspector.cs     Linux 环境检测
  Data/                     Identity 与 SQLite 迁移
tests/CraftHome.Tests/       关键自动测试
tests/browser/              实际浏览器开服流程测试
deploy/linux/               Linux 安装、升级与引擎初始化
scripts/                    SSH 访问辅助脚本
```

## 上游与范围

- 管理引擎：[MCSManager](https://github.com/MCSManager/MCSManager)。源码按其自身许可证提供，本项目通过 HTTP API 集成，没有修改上游核心。
- 账户、Blazor 和 ORM：微软 ASP.NET Core／Identity／EF Core 模板及组件。
- 游戏运行：测试使用独立的 Paper 实例；具体版本、参数和验证边界以测试报告为准。

源码仓库：[b-h-r123/CraftHome](https://github.com/b-h-r123/CraftHome)。项目许可证尚待选择，上游组件保留各自许可证；本项目仍处于初版阶段。
