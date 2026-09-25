# HDRCapture

Windows 11 x64 的 HDR 区域截图工具。使用 Windows Graphics Capture 冻结所有显示器，
把经过 SDR 白点归一化和高光滚降的 LDR 图片放入剪贴板；需要时还可同时保存线性 HDR EXR。
v1.2 增加了按需的 HDR 降噪和本地 ComfyUI 人像美化。

## 使用

运行发布目录中的 `HDRCapture.exe`，无需安装，也不需要单独安装 .NET：

```text
HDRCapture.exe
```

- 默认快捷键：`Ctrl+Alt+F10`
- 托盘图标左键单击：开始截图
- 托盘菜单：截图、编辑上次截图、打开保存目录、设置、退出
- 拖拽松开确认选区，`Esc` 或鼠标右键取消
- 默认只复制 LDR 图片到剪贴板；可在设置中勾选自动保存 HDR EXR
- EXR 默认保存到 `HDRCapture.exe` 同级的 `picture` 子目录，也可在设置中自定义
- 预览画质可选全分辨率、1/2 分辨率或最长边 1920 像素
- 配置文件：`%LOCALAPPDATA%\HDRCapture\settings.json`

EXR 为未压缩 FP16、`B/G/R` 通道、线性 scRGB，`1.0` 对应 80 nit。剪贴板同时提供
注册的 `PNG` 格式和 `CF_DIBV5`，Photoshop、浏览器和常见图像编辑器均可粘贴。

## 后期处理

托盘菜单中的“编辑上次截图”会打开后期处理窗口：

- 磨皮：控制 FaceDetailer 的局部重绘与融合强度
- 降噪：快速预览使用边缘保护算法，最终输出使用分块 OIDN
- 提亮：在色调映射前应用，不会覆盖 EXR 中大于 `1.0` 的高光
- 细节保留：控制原脸高频纹理回填比例

程序内存中只保留最近一张完整 HDR 截图。再次截图时，上一张 HDR 图像会被释放。
AI 结果只显示在后期窗口中，只有点击复制或保存后才会替换剪贴板或写入文件，原始 EXR
永远不会被降噪或 AI 结果覆盖。

剪贴板本身只保存当前一张图。每次写入前都会调用 `EmptyClipboard`，因此不会随着反复截图
累积多张图片；系统也没有一个由本程序控制的固定 “400 MB 上限”。剪贴板内存由 Windows
和当前持有剪贴板数据的进程管理，下一次写入会替换上一份数据。

## ComfyUI 人像美化

首次点击“人像美化”时需要配置本机 ComfyUI 根目录，例如 `D:\AI\ComfyUI`。程序会检测：

- `ComfyUI\main.py`
- `python_embeded\python.exe`
- ComfyUI-Impact-Pack
- `models\ultralytics\bbox\face_yolov8m.pt`
- `models\sams\sam_vit_b_01ec64.pth`

路径有效且服务未运行时，HDRCapture 会用隐藏窗口启动 ComfyUI。已有服务会被复用，
用户自己启动的 ComfyUI 进程不会被强制关闭。只有 HDRCapture 启动的服务会在空闲后关闭。

HDRCapture 只使用自己的极简 API 工作流模板，保存在：

```text
%LOCALAPPDATA%\HDRCapture\comfy-workflows
```

它不会读取、覆盖或修改 `ComfyUI\user\default\workflows\00_总控台.json`，也不会修改
`comfy.settings.json`。人像工作流固定内部提示词和采样参数，界面只让用户选择检查点模型。

如果已安装 `ComfyUI_IPAdapter_plus` 和 CLIP-Vision，程序可在后期窗口主动提供
IP-Adapter Face 可选组件。下载前会显示来源、大小和 SHA-256，下载后再进行校验。
未安装该可选组件时，FaceDetailer、HDR 降噪、提亮和轻量锐化仍可完整使用。

## 命令

```powershell
# 显示器、DPI、HDR、SDR 白点、WGC、OIDN 和 ComfyUI 诊断
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

发布配置为 `win-x64` 自包含、不裁剪单文件。OIDN 使用内置 CPU 运行时，
第三方许可证见 `THIRD-PARTY-NOTICES.md`。

## 边界

- 仅支持 Windows 11 x64。
- 独占全屏、DRM 或受保护视频不保证可捕获。
- 当前版本不包含标注、OCR、上传、录屏、多区域截图和压缩 EXR。
- 未压缩 EXR 的磁盘占用约为每个像素 6 字节。
- ComfyUI 缺失或不支持时，截图、剪贴板和 HDR 降噪仍可正常使用。
