# 构建说明

本仓库只依赖 Windows 自带组件，不需要安装 Visual Studio 或 .NET SDK。

## 前置条件

| 组件 | 说明 |
| --- | --- |
| Windows 10 / 11 | 构建脚本调用 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`（.NET Framework 4.8 自带） |
| PowerShell 5.1 | 系统自带。**不要**用 PowerShell 7 直接跑：部分脚本依赖 5.1 的编码行为 |
| NSIS 3.x | 仅打安装包时需要，`winget install NSIS.NSIS` |

> ⚠️ **编码陷阱**：仓库内所有 `.ps1` / `.nsi` 都是 **UTF-8 with BOM**。
> Windows PowerShell 5.1 在没有 BOM 时会按系统 ANSI 码页（简体中文系统是 GBK）解析脚本，
> 于是脚本里所有中文——包括中文 exe 名 `并川听澜.exe`——都会变成乱码，构建出 `??? .exe`。
> 如果你用编辑器改过脚本后发现中文全乱了，运行：
>
> ```powershell
> powershell -NoProfile -ExecutionPolicy Bypass -File packaging\fixbom.ps1 -Path build.ps1,packaging\build.ps1
> ```

## 1. 编译主程序

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

产物：

```
build\并川听澜.exe
build\NAudio.dll      ← 必须和 exe 放在一起，.NET 不会去上级目录找依赖
```

脚本会在编译后做以下断言，任何一条失败都会以非零码退出：

| 断言 | 为什么必须有 |
| --- | --- |
| 托管资源含 `AppIcon.ico` | 没有它 `Form.Icon` 会静默退回 WinForms 默认图标，任务栏显示成通用 exe |
| 托管资源含 `Avatar0..2.png` | 头像缺失时界面会出现空白圆形 |
| PE 里含 `permonitorv2` | 否则进程是 DPI 无感知的，125%/150% 下整窗口被位图拉伸 |
| PE 里含 `asInvoker` | 自带 manifest 时若漏写，Windows 会对文件/注册表写入启用 UAC 虚拟化 |
| `NAudio.dll` 存在 | 缺它编译能过，但一启动就在 `AudioEngine` 构造函数里 `FileNotFoundException` |

### 编译期排除的文件

`src\IconStudio.cs` 有独立的 `Main`，是设计期的图标生成器，**不参与主构建**
（`build.ps1` 里 `$Exclude = @('IconStudio.cs')`）。需要重新生成图标时单独编译它。

## 2. 打安装包

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File packaging\build.ps1
# 可选参数：
#   -Version 1.2.3        改版本号（会同步写进安装包文件名与版本资源）
#   -SkipCompile          跳过重新编译，直接拿现有 build\ 打包
#   -ProjectRoot <path>   指定项目根目录
```

产物：

```
packaging\dist\BingchuanTinglan-1.2-Setup.exe
packaging\dist\BingchuanTinglan-1.2-Setup.exe.sha256
```

`makensis` 的查找顺序：`packaging\_tools\nsis-3.11\makensis.exe` → `PATH` → `C:\Program Files (x86)\NSIS`。
仓库**不提交** NSIS 二进制（11 MB 的第三方工具链），请自行安装。

打包脚本会：

1. 从 `build\` 收集 payload（exe + dll + ico），缺文件就报错
2. 计算 payload 的 SHA256 与字节数，生成 `installer\payload-fingerprint.nsh`
3. 调 `makensis` 编译 `installer\installer.nsi`
4. 校验产物的 PE 头、UAC manifest、嵌入图标、版本资源四段式、体积下限
5. 写出 `.sha256`（无 BOM UTF-8 —— 带 BOM 会让 `certutil` 把 BOM 当成哈希首字符）

安装器运行时还会用第 2 步的指纹做**逐字节校验**，装出来和打包时不一致会当场报错。

## 3. 版本号要改三处

改版本号时必须同步修改，否则文件属性、安装包文件名、控制面板里的显示版本会互相打架：

| 位置 | 字段 | 格式 |
| --- | --- | --- |
| `src\AssemblyInfo.cs` | `AssemblyVersion` / `AssemblyFileVersion` | 四段 `1.2.0.0` |
| `src\AssemblyInfo.cs` | `AssemblyInformationalVersion` | 可写两段 `1.2` |
| `packaging\installer\installer.nsi` | `VER`（以及 `build.ps1 -Version`） | `1.2` |

Windows 版本资源只认 `X.X.X.X`；`VIProductVersion` 同理，`packaging\build.ps1` 会自动把两段补齐成四段。

## 4. 发布

```powershell
git tag v1.2
git push origin v1.2
```

打 tag 后 GitHub Actions 会自动编译并在 Release 里附上 exe 与安装包（见 `.github/workflows/build.yml`）。
也可以手动：在 GitHub 网页上 Releases → Draft a new release，把 `packaging\dist\` 里的两个文件拖进去。

## 5. 常见问题

**Q：编译报 `error CS2001: 找不到源文件`？**
A：路径里有中文或空格。`build.ps1` 用 `$PSScriptRoot` 定位，从任何目录调用都可以；
但如果你的编辑器把脚本另存成了别的编码，先把 BOM 修回来（见上面的 `fixbom.ps1`）。

**Q：编出来的 exe 是 `???.exe` 这种名字？**
A：BOM 丢了，PowerShell 5.1 把中文按 GBK 解析成了乱码。同上，跑 `fixbom.ps1`。

**Q：装完之后任务栏图标是白板？**
A：说明 `/win32icon` 生效了但托管资源 `AppIcon.ico` 没进程序集。
`build.ps1` 里的 `ICON_RESOURCE_PRESENT` 断言就是防这个的，正常构建不会出现。

**Q：程序启动就闪退，没有窗口？**
A：看 `%APPDATA%\MultiAudioOutput\crash.log`。最常见的原因是 `NAudio.dll` 没和 exe 放在同一目录。
