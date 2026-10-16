# DiskSweep

DiskSweep 是一个 Windows 磁盘扫描与安全清理工具，采用“先扫描、再选择、最后确认”的流程。

## 下载与运行

从 [Releases](https://github.com/Zppppp124/DiskSweep/releases/latest) 下载最新版：

`DiskSweep.exe`

程序需要管理员权限，启动时会提示 UAC。EXE 为自包含单文件版本，不需要额外安装 .NET。

当前版本：`v1.0.0`

SHA-256：

`047EDDA00D5DE0DB57F499854E4EB2D53066AD5F9856E8EEB3F518A8FD7205E0`

## 清理原则

- 临时文件只删除 7 天前的普通内容。
- 默认只勾选用户临时文件、Windows 临时文件和崩溃转储。
- pip、npm、Yarn、pnpm、Go、Gradle、NuGet 等缓存需要手动勾选。
- 回收站、Windows 更新缓存、安装器缓存需要手动勾选并二次确认。
- 模型缓存、休眠文件和 Windows 组件存储默认不勾选。
- 不扫描或删除项目源代码、文档、浏览器配置和聊天记录。
- 跳过符号链接、被占用文件和不满足规则根目录校验的路径。

## 日志

清理日志位于：

`%LOCALAPPDATA%\DiskSweep\logs`

界面的“打开日志目录”按钮可以直接打开该位置。

## 构建

在项目目录中运行：

```powershell
dotnet publish .\DiskSweep.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None -p:DebugSymbols=false -o .\publish
```
