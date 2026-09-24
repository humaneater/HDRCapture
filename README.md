# HDRCapture

Windows 11 x64 的 HDR 区域截图工具。使用 Windows Graphics Capture 冻结所有显示器，
把经过 SDR 白点归一化和高光滚降的 LDR PNG 放入剪贴板；需要时还可同时保存线性 HDR EXR。

## 使用

运行发布版：

```text
bin\Release\net9.0-windows\win-x64\publish\HDRCapture.exe
```

- 默认快捷键：`Ctrl+Alt+F10`
- 托盘图标左键双击：开始截图
- 托盘菜单：截图、打开保存目录、设置、退出
- 拖拽松开确认选区，`Esc` 或鼠标右键取消
- 默认只复制 LDR 图片到剪贴板，不保存 EXR
- 在设置中勾选“保存 HDR EXR 到本地目录”后，EXR 默认保存到 `HDRCapture.exe` 同级的 `picture` 子目录
- 预览画质可选全分辨率、1/2 分辨率或最长边 1920 像素
- 配置文件：`%LOCALAPPDATA%\HDRCapture\settings.json`

EXR 为未压缩 FP16、`B/G/R` 通道、线性 scRGB，`1.0` 对应 80 nit。剪贴板同时提供
注册的 `PNG` 格式和 `CF_DIBV5`。

发布目录中的 `HDRCapture.exe` 是自包含单文件版本，可直接运行，无需安装或单独安装 .NET。

## 命令

```powershell
# 显示器、DPI、HDR、SDR 白点和 WGC 帧格式诊断
HDRCapture.exe --diagnostics

# 无第三方依赖的内部测试
HDRCapture.exe --self-test

# 无界面区域截图；指定 --out 时会强制保存 EXR，主要用于验证
HDRCapture.exe --capture-region 100,100,800,600 --out .\picture\verify.exr
```

## 构建

需要 .NET 9 SDK：

```powershell
dotnet build .\HDRCapture.csproj -c Debug
dotnet publish .\HDRCapture.csproj -c Release -r win-x64 --self-contained true
```

发布配置为 `win-x64` 自包含、不裁剪单文件。

## 边界

- 仅支持 Windows 11 x64。
- 独占全屏、DRM 或受保护视频不保证可捕获。
- 当前版本不包含标注、OCR、上传、录屏、多区域截图和压缩 EXR。
- 未压缩 EXR 的磁盘占用约为每个像素 6 字节。
