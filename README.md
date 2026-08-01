# AutoDim — AutoCAD 2025 自动尺寸标注插件

用 C# / .NET 8 开发的 AutoCAD 2025 插件，自动为轮廓、孔/圆等生成尺寸标注。分阶段交付，目标覆盖：
① 总体外形尺寸 ② 轮廓各段尺寸 ③ 孔/圆直径与定位 ④ 相邻边夹角；支持三种触发方式：先选对象、整图、框选。

当前进度：**五个阶段（Phase 0~5）的代码均已实现**：总体外形尺寸、轮廓分段、孔/圆直径与定位、
相邻边夹角（任意倾斜多边形模式）、防重叠清理 / 配置项 / 自动比例 / 幂等刷新等打磨。

**已知限制**：目前只对规整、简单的图纸（矩形+孔、单个多边形等）标注可靠；复杂图纸
（多轮廓相交、杂乱线框、重复线等）仍无法稳定自动标注——这也是项目暂停个人开发的直接原因。
仓库保持公开，欢迎有能力的人接手继续完善，路线图见文末。

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
  Commands.cs           命令入口 + 加载横幅（AUTODIM / ADIMALL / ADIMWIN / ADIMSEL / ADIMSAMPLE / ADIMCOORD / ADIMSCALE / ADIMCFG / ADIMDEBUG / ADIMCLEAN / ADIMCLN / ADIMDUMP）
  SampleBuilder.cs      生成测试样例图（ADIMSAMPLE）
  Selection/            三种触发方式取集
  Dimensioning/         标注引擎与各类别 Dimensioner（总体/分段/孔圆/坐标/任意多边形）
  Cad/                  图层、标注样式助手
  Config/               运行选项
src/AutoDim.Core/       纯几何清洗库（不依赖 AutoCAD，可独立单测）：去重/微段合并/端点吸附/闭合轮廓提取
deploy/                 AutoCAD ApplicationPlugins 自动加载清单
picture/                示例截图等图片资源
test/
  run-headless.scr      accoreconsole 脚本：SECURELOAD=0 + NETLOAD + 建样例 + 标注 + 清洗
  run-tests.sh          编译 → 无界面运行 → 断言
  render_dxf.py         DXF → PNG 渲染（原始几何黑色、标注红色，支持局部放大）——MLLM 语义层前置
  AutoDim.Core.SelfTest/  几何清洗库自测（dotnet run --project test/AutoDim.Core.SelfTest）
build.sh                dotnet build 封装
```

## 图纸渲染（MLLM 语义层前置）

视觉模型要"看图"才能分类轮廓/孔/填充噪声，因此先把 DWG 无界面导出为 DXF、再用 Python 渲染成 PNG：

```bash
# 1) 无界面导出 DXF（test/dxfout.scr 导出原始图；test/adim_render.scr 先 ADIMCLEAN 再导出标注图）
powershell -File test/run-headless.ps1 -InputDwg test.dwg -Script test/adim_render.scr -Log test/r.log

# 2) 渲染 PNG（整图或局部：zoom=x0,y0,x1,y1）
python test/render_dxf.py test/test_annotated.dxf out.png
python test/render_dxf.py test/test_annotated.dxf out_zoom.png 5800,150,6100,400
```

渲染验证（test.dwg）：整图 1875×242（宽扁图），局部放大 1401px 高；标注以红色叠加在黑色原始几何上，
可直接用于视觉模型分类/质检。

### MLLM 语义层可行性（2026-08 实测）

本地视觉模型（ollama + qwen2.5vl:3b，RTX 5060 8GB）对机械线框图的实测结论：

| 能力 | 3B 实测 | 7B 实测 | 结论 |
|------|---------|---------|------|
| 合成图"有无噪声"二分类 | 4/4 对 | 4/4 对 | ✅ 均可用 |
| 圆孔计数（合成图 5/12 个） | 答 1/6 | 答 4/5 | ❌ 均不可用（几何方法数得更准） |
| 真实图纸线条密度（清洗前 2.3% vs 后 0.4% 墨迹） | 均答"少" | 4/4 区域"多→少" | ❌ 3B / ✅ 7B |
| 清洗质量哨兵（自动采样 4 区域） | - | 3/4 正确 | ✅ 可用作辅助信号 |
| 标注质量分级 | 无区分度 | - | ❌ 不可用 |

结论：**7B 视觉模型能可靠区分"清洗前密集/清洗后稀疏"（密度/噪声判断），可做清洗质量哨兵**；
计数与精确定位不可靠，交给几何方法。哨兵用法：
`python test/mllm_sentinel.py test_out.dxf test_annotated.dxf qwen2.5vl:7b`
（先按上文导出原始/清洗后 DXF；输出各区域"原始=多 清洗后=少"的清洗有效信号。）

## 图纸清洗（ADIMCLEAN / AutoDim.Core）

复杂图纸（扫描矢量化、碎线段、重复重叠图元）无法直接标注，先用清洗管线把图"读干净"：

```text
脏线段 → 端点吸附 + 去重 → 共线微段合并 → 平面图闭合轮廓提取（圆弧以 bulge 保留）+ 圆去重
```

- `ADIMCLEAN` 命令：对选中对象/整图执行清洗，把闭合轮廓与去重圆绘制到 `ADIM_CLEAN` 图层，并打印统计。
  清洗完成后会对清洗结果**按特征组分别自动标注**（一条龙：脏图 → 干净几何 → 逐特征标注）。
  也可在 `ADIMCFG` 里选 `C` 开启"自动清洗"：之后 `AUTODIM`/`ADIMALL`/`ADIMWIN`/`ADIMSEL`
  会先自动清洗再标注，一条命令走完。
- 圆弧整段以 bulge 保留（不采样成碎弦），清洗后的轮廓仍含圆角，可自动标注圆角半径（`arc=` 计数）。
- `AutoDim.Core`：纯 C# 几何库（无 AutoCAD 依赖、无 NuGet 包），可独立自测：
  `dotnet run --project test/AutoDim.Core.SelfTest`（覆盖去重/微段合并/内外孔双面/圆去重）。
- 已知限制：像 test.dwg 这类矢量化碎片图**没有全局闭合外轮廓**，清洗按"逐特征闭合"处理；
  填充/剖面线笔触的识别（噪声过滤）与语义分类是下一步工作。

### 当前效果（test.dwg 实测）

```text
输入线段(含块展开)  8975
清洗后              6298
闭合面(含圆弧bulge) 579（共线误并修复 +193、LinkGap 0.025% 再闭合 +25）
去重圆              447
特征组              125
自动标注总量        落地 297 个尺寸 + 64 个 N×Ød 注记（生成 312、去重 15）
ADIM 文字撞车对数   0（真实文字外接框相交，可读性指标）
无界面运行耗时      约 17s
```

布局质量改进（2026-08）：

- **重复直径合并为数量注记**：test.dwg 的 447 个孔只有 6 种直径（128×Ø2.75、128×Ø3.5、
  128×Ø4.25、48×Ø2.5…），原来每个孔都引线标 ⌀（442 个），现同径 ≥2 只出 "N×Ød"
  注记（61 个），直径标注减到 111 个——GB 惯例 "N×Ød" 注法。
- **斜边主投影**：斜边只标较大的直角边（水平或垂直投影），不再双投影自相压线。
- **生成后布局收尾**：完全/近重复标注去重、尺寸线沿"远离被测要素"方向外推避让，
  注记按字高错开、代表孔引线扇形展开（最小 90° 夹角）。
- **文字撞车指标**：以标注文字实体外接框的相交对数衡量排版质量（AABB 重叠对长尺寸链
  交叉虚高，X/Y 定位链角部交叉但图形并不相碰）。
- **定位链微段跳过**：链段跨度 < 3mm 的文字放不下、会被外置到界线外与邻段互碰，直接跳过。

过滤规则（`AutoDim.Core` 阈值均可配）：长度 <3mm 且不属于闭合面的线段丢弃；
面积 <1mm² 的碎环丢弃；周长²/面积 >60 的细长碎面丢弃；中心线/虚线/点划线等辅助图层
默认不参与轮廓提取；组内面全部 <50mm² 的小碎面组只标总体+孔、不标分段尺寸。
图纸太脏/太松可用 `ADIMCLN` 调整清洗参数（不同图纸松紧不同）。

尚未完成：尺寸避让布局优化（密集区标注仍可能互相重叠）、MLLM 语义层
（识别"哪片是轮廓/孔/填充噪声"，MechVQA 一类能力）、3D(STEP) 与知识库路线。

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
脚本会：编译 → 用 `accoreconsole.exe`（SECURELOAD=0 + NETLOAD，输入为仓库内 `test.dwg`）
执行 `ADIMSAMPLE` / `ADIMALL` / `ADIMCLEAN` → 断言输出包含 `ADIMSAMPLE:`、`AUTODIM:`、`ADIMCLEAN:`
且 `total>0`，通过则打印 `== PASS ==`。

> 无界面运行踩过的坑（已修复）：模板 `acadiso.dwt` 首次初始化会卡住控制台；`SECURELOAD=1`
> 会静默拒绝 NETLOAD；accoreconsole 从 Git Bash 直接启动不读脚本（需经
> [run-headless.ps1](test/run-headless.ps1) 用 PowerShell 启动）；PS1 文件需保持纯 ASCII。

## 在 AutoCAD 中手动加载试用

1. 打开 AutoCAD 2025。
2. 命令行输入 `NETLOAD`，选择 `src\AutoDim\bin\x64\Debug\net8.0-windows\AutoDim.dll`。
   - 若弹出安全加载提示：这是 `SECURELOAD` 机制。可将该 `bin` 目录加入「选项→文件→受信任位置」，或临时将系统变量 `SECURELOAD` 设为 0。
3. 加载成功后命令行会显示 `AutoDim 已加载...`。
4. 试用：
   - `ADIMSAMPLE` 生成一张测试图；
   - `AUTODIM`：若命令前已选对象则直接标注，否则按提示选择 `选择对象(S)/整图(A)/窗口(W)`；
   - `ADIMALL` 整图、`ADIMWIN` 框选、`ADIMSEL` 选中对象。
   - 倾斜/异形零件可试 `ADIMCOORD`（坐标标注）或 `ADIMSCALE`（固定比例）、`ADIMCFG`（类别开关）。
   - 标注生成在 `ADIM` 图层（绿色），一次 `U` 可整批撤销。

## 命令一览

| 命令 | 说明 |
|---|---|
| `AUTODIM` | 主命令；pickfirst 优先，否则询问选择方式 |
| `ADIMALL` | 对整张图自动标注 |
| `ADIMWIN` | 框选区域内标注 |
| `ADIMSEL` | 对选中对象标注 |
| `ADIMSAMPLE` | 生成复杂复合测试图（L 形+圆角+斜边+3 孔+远距离小件+任意倾斜多边形） |
| `ADIMCOORD` | 坐标标注：对任意倾斜多边形按最左下顶点引 X/Y 坐标（复杂板类推荐画法） |
| `ADIMSCALE` | 设置/清除固定 Dimscale（默认按包围盒自适应 0.5~3.0） |
| `ADIMCFG` | 切换标注类别开关（总体/分段/孔圆/全部/清空）+ 自动清洗开关(C)，持久化到图 |
| `ADIMDEBUG` | 打印多段线几何诊断信息，用于核对标注算法 |
| `ADIMCLEAN` | 图纸清洗+标注一条龙：去重/微段合并/闭合轮廓提取 → 绘制到 ADIM_CLEAN 图层 → 自动标注 |
| `ADIMCLN` | 调整清洗参数（吸附/合并公差、最短保留长度、最小面积、最大细长度、开放链闭合间距），持久化到本图 |

## 路线图（五个阶段代码已完成，后续重点在鲁棒性）

- [x] Phase 0 脚手架 / 可加载骨架 / 无界面测试循环
- [x] Phase 1 总体外形尺寸 + 三种触发方式
- [x] Phase 2 孔/圆 直径 + 定位
- [x] Phase 3 轮廓各段尺寸（直线段对齐 / 圆弧半径）
- [x] Phase 4 相邻边夹角（任意倾斜多边形模式输出夹角；正交路径按 GB/T 4458.4 不重复标角度）
- [x] Phase 5 打磨（防重叠清理、配置项、自动比例、幂等刷新）
- [x] 复杂图纸第一步：几何清洗库 AutoDim.Core + ADIMCLEAN 命令（去重/合并/逐特征闭合）
- [ ] 复杂图纸鲁棒性：多轮廓 / 相交 / 杂乱线框的识别与尺寸避让
