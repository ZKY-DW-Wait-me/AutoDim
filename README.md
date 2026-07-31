# AutoDim — AutoCAD 2025 自动尺寸标注插件

用 C# / .NET 8 开发的 AutoCAD 2025 插件，自动为轮廓、孔/圆等生成尺寸标注。分阶段交付，目标覆盖：
① 总体外形尺寸 ② 轮廓各段尺寸 ③ 孔/圆直径与定位 ④ 相邻边夹角；支持三种触发方式：先选对象、整图、框选。

当前进度：**Phase 0（脚手架）+ Phase 1（总体外形尺寸 + 三种触发方式）**。项目仍处于早期阶段，欢迎接手继续开发，路线图见文末。

---

## 环境要求

- **AutoCAD 2025**（程序集版本 25.0.58）。编译时需要本机安装 AutoCAD；默认从
  `D:\Program Files\AutoCAD 2025\` 引用托管程序集，若安装路径不同，可通过环境变量
  `ACAD_DIR` 覆盖（注意需以 `\` 结尾），例如：
  `ACAD_DIR="C:\Program Files\Autodesk\AutoCAD 2025\" bash build.sh`
- **.NET 8 SDK**（用于 `dotnet build`）。AutoCAD 2025 是首个基于 .NET 8 的版本，故目标框架为 `net8.0-windows`、平台 x64。

> 引用策略：只引用 `acdbmgd` + `accoremgd`（不引用 `acmgd`），使插件既能在完整 AutoCAD 中 `NETLOAD` 运行，也能在 `accoreconsole` 无界面测试中运行。

## 目录结构

```
src/AutoDim/            插件源码
  Commands.cs           命令入口 + 加载横幅（AUTODIM / ADIMALL / ADIMWIN / ADIMSEL / ADIMSAMPLE）
  SampleBuilder.cs      生成测试样例图（ADIMSAMPLE）
  Selection/            三种触发方式取集
  Dimensioning/         标注引擎与各类别 Dimensioner（当前：总体外形）
  Cad/                  图层、标注样式助手
  Config/               运行选项
deploy/                 AutoCAD ApplicationPlugins 自动加载清单
picture/                示例截图等图片资源
test/
  run-headless.scr      accoreconsole 脚本：bundle 自动加载 + 建样例 + 整图标注
  run-tests.sh          编译 → 无界面运行 → 断言
build.sh                dotnet build 封装
```

## 编译

```bash
bash build.sh            # Debug
bash build.sh Release    # Release
```
或在 Visual Studio 2026 中打开 `AutoDim.sln`，选择 `Debug|x64` 生成。
产物：`src/AutoDim/bin/x64/Debug/net8.0-windows/AutoDim.dll`

## 无界面回归测试（不必打开 AutoCAD）

```bash
bash test/run-tests.sh
```
脚本会：编译 → 用 `accoreconsole.exe` 加载 DLL、生成 100×60 矩形+2 孔的样例、对整图执行标注 →
断言输出包含 `ADIMSAMPLE:`、`overall=2`、`total=2`，通过则打印 `== PASS ==`。

## 在 AutoCAD 中手动加载试用

1. 打开 AutoCAD 2025。
2. 命令行输入 `NETLOAD`，选择 `src\AutoDim\bin\x64\Debug\net8.0-windows\AutoDim.dll`。
   - 若弹出安全加载提示：这是 `SECURELOAD` 机制。可将该 `bin` 目录加入「选项→文件→受信任位置」，或临时将系统变量 `SECURELOAD` 设为 0。
3. 加载成功后命令行会显示 `AutoDim 已加载...`。
4. 试用：
   - `ADIMSAMPLE` 生成一张测试图；
   - `AUTODIM`：若命令前已选对象则直接标注，否则按提示选择 `选择对象(S)/整图(A)/窗口(W)`；
   - `ADIMALL` 整图、`ADIMWIN` 框选、`ADIMSEL` 选中对象。
   - 标注生成在 `ADIM` 图层（绿色），一次 `U` 可整批撤销。

## 命令一览

| 命令 | 说明 |
|---|---|
| `AUTODIM` | 主命令；pickfirst 优先，否则询问选择方式 |
| `ADIMALL` | 对整张图自动标注 |
| `ADIMWIN` | 框选区域内标注 |
| `ADIMSEL` | 对选中对象标注 |
| `ADIMSAMPLE` | 生成测试图（100×60 矩形 + 2 孔 R8） |

## 路线图

- [x] Phase 0 脚手架 / 可加载骨架 / 无界面测试循环
- [x] Phase 1 总体外形尺寸 + 三种触发方式
- [ ] Phase 2 孔/圆 直径 + 定位
- [ ] Phase 3 轮廓各段尺寸（直线段对齐 / 圆弧半径）
- [ ] Phase 4 相邻边夹角
- [ ] Phase 5 打磨（防重叠、配置项、自动比例、幂等刷新）
