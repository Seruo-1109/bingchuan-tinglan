using System.Reflection;

// ─────────────────────────────────────────────────────────────────────────────
// 程序集版本信息（显示在 文件属性 → 详细信息 里）
//
// 为什么单独建这个文件：
// 之前整个工程没有任何 assembly: 特性，编出来的 exe 版本资源是**空的** ——
// 文件属性里 FileVersion/ProductVersion 都是 0.0.0.0、ProductName 是空白。
// 于是「这个 exe 是哪个版本」只能靠文件名和安装包猜，回滚时极易搞混。
//
// 三处版本号的分工（改版本时必须一起改，见 打包工程\build.ps1 顶部的说明）：
//   AssemblyFileVersion        → 文件属性里的「文件版本」，必须是四段 1.2.0.0
//   AssemblyInformationalVersion → 文件属性里的「产品版本」，可以写 1.2 这种短形式
//   installer.nsi 的 VER / VER4 → 安装包文件名 与 控制面板「程序和功能」里的显示版本
// ─────────────────────────────────────────────────────────────────────────────

[assembly: AssemblyTitle("并川听澜")]
[assembly: AssemblyDescription("把声音同时送到多个输出设备")]
[assembly: AssemblyProduct("并川听澜")]
[assembly: AssemblyCompany("并川听澜")]
[assembly: AssemblyCopyright("© 2026 并川听澜")]
[assembly: AssemblyTrademark("")]

// 四段式：Windows 版本资源只认 X.X.X.X，写 1.2 会被静默当成 1.2.0.0 但界面显示不一致。
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

// 用户可见的版本号，保持和安装包文件名、欢迎页胶囊文字一致（都是 1.2）。
[assembly: AssemblyInformationalVersion("1.2")]
