using System.Text;
using System.Text.Json.Nodes;
using HdrCapture.Capture;
using HdrCapture.ComfyUi;
using HdrCapture.Configuration;
using HdrCapture.Exr;
using HdrCapture.Imaging;
using HdrCapture.Infrastructure;

namespace HdrCapture.Core;

/// <summary>Built-in dependency free test suite: HDRCapture.exe --self-test</summary>
internal static class SelfTestRunner
{
    private static readonly List<(string Name, string? Failure)> Results = [];

    public static int Run(ConsoleHost console)
    {
        Results.Clear();

        RunTest("half-float 转换", TestHalfFloatConversion);
        RunTest("EXR 写入/读取", TestExrRoundTrip);
        RunTest("负坐标裁切", TestNegativeCoordinateCrop);
        RunTest("跨屏拼接与空隙", TestCrossMonitorStitching);
        RunTest("SDR 白点归一化", TestSdrWhitePointNormalization);
        RunTest("中性色调映射", TestNeutralTonemap);
        RunTest("文件命名去重", TestFileNaming);
        RunTest("配置损坏恢复", TestSettingsRecovery);
        RunTest("预览画质档位", TestPreviewQuality);
        RunTest("EXR 保存开关", TestExrSaveSwitch);
        RunTest("保存目录错误", TestSaveDirectoryFailure);
        RunTest("最近截图替换", TestLastCaptureStore);
        RunTest("快速 HDR 降噪", TestFastHdrDenoise);
        RunTest("OIDN HDR 降噪", TestOidnHdrDenoise);
        RunTest("ComfyUI 工作流结构", TestComfyUiWorkflow);
        RunTest("ComfyUI 本地工作流模板", TestComfyUiWorkflowTemplate);
        RunTest("ComfyUI 错误提示", TestComfyUiErrorText);
        RunTest("ComfyUI 路径检测", TestComfyUiPathValidation);
        RunTest("ComfyUI 可选组件", TestComfyUiOptionalComponents);

        var builder = new StringBuilder();
        var failures = 0;
        foreach (var (name, failure) in Results)
        {
            if (failure is null)
            {
                builder.AppendLine($"PASS  {name}");
            }
            else
            {
                failures++;
                builder.AppendLine($"FAIL  {name}: {failure}");
            }
        }

        builder.AppendLine($"{Results.Count - failures}/{Results.Count} passed");
        console.WriteLine(builder.ToString().TrimEnd());
        return failures == 0 ? 0 : 1;
    }

    private static void RunTest(string name, Action test)
    {
        try
        {
            test();
            Results.Add((name, null));
        }
        catch (Exception ex)
        {
            Results.Add((name, ex.Message));
        }
    }

    private static void TestHalfFloatConversion()
    {
        AssertEqual(0x3C00, RegionComposer.ToHalfBits(1.0f), "1.0 → half");
        AssertEqual(0x0000, RegionComposer.ToHalfBits(0.0f), "0.0 → half");
        AssertEqual(0x3C01, RegionComposer.ToHalfBits(1.0009765625f), "1.0009765625 → half");
        AssertEqual(0xC000, RegionComposer.ToHalfBits(-2.0f), "-2.0 → half");

        foreach (var value in new[] { 0.1f, 0.5f, 3.6f, 9.75f, 1000f })
        {
            var roundTrip = RegionComposer.FromHalfBits(RegionComposer.ToHalfBits(value));
            var tolerance = MathF.Abs(value) * 0.001f;
            AssertTrue(
                MathF.Abs(roundTrip - value) <= tolerance,
                $"{value} 往返误差过大：{roundTrip}");
        }
    }

    private static void TestExrRoundTrip()
    {
        const int width = 5;
        const int height = 3;
        var image = new LinearImage(width, height);
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                var index = (row * width) + column;
                image.Red[index] = RegionComposer.ToHalfBits(column + 0.5f);
                image.Green[index] = RegionComposer.ToHalfBits(row + 1.0f);
                image.Blue[index] = RegionComposer.ToHalfBits(0.125f);
            }
        }

        image.Regions.Add(new WhitePointRegion(0, 0, width, height, @"\\.\DISPLAY9", true, 240));

        var path = Path.Combine(Path.GetTempPath(), $"hdrcapture-selftest-{Guid.NewGuid():N}.exr");
        try
        {
            var metadata = new ExrMetadata(
                DateTimeOffset.Now,
                new PixelRect(-10, -20, width, height),
                @"\\.\DISPLAY9 HDR 240nit",
                240,
                0.5,
                "self-test");
            OpenExrWriter.WriteAtomic(path, image, metadata);

            AssertTrue(File.Exists(path), "EXR 文件未生成");
            AssertTrue(
                !File.Exists(path + ".tmp"),
                "临时文件未被清理");

            var loaded = OpenExrReader.Read(path);
            AssertEqual(width, loaded.Width, "dataWindow 宽度");
            AssertEqual(height, loaded.Height, "dataWindow 高度");
            AssertEqual("B,G,R", string.Join(",", loaded.ChannelNames), "通道顺序");
            foreach (var channel in loaded.ChannelNames)
            {
                AssertEqual(1, loaded.ChannelTypes[channel], $"{channel} 应为 HALF");
            }

            AssertEqual(0, loaded.Attributes["compression"][0], "压缩方式应为 NO_COMPRESSION");
            AssertEqual(0, loaded.Attributes["lineOrder"][0], "lineOrder 应为 INCREASING_Y");
            AssertTrue(loaded.Attributes.ContainsKey("software"), "software 属性缺失");
            AssertTrue(loaded.Attributes.ContainsKey("colorSpace"), "colorSpace 属性缺失");

            var chromaticities = loaded.Attributes["chromaticities"];
            AssertEqual(32, chromaticities.Length, "chromaticities 长度");
            AssertClose(0.64f, BitConverter.ToSingle(chromaticities, 0), 1e-6f, "red.x");
            AssertClose(0.60f, BitConverter.ToSingle(chromaticities, 12), 1e-6f, "green.y");
            AssertClose(0.06f, BitConverter.ToSingle(chromaticities, 20), 1e-6f, "blue.y");
            AssertClose(0.3127f, BitConverter.ToSingle(chromaticities, 24), 1e-6f, "white.x");

            AssertClose(2.5f, HalfAt(loaded, "R", 2, 1), 1e-3f, "R 像素");
            AssertClose(2.0f, HalfAt(loaded, "G", 2, 1), 1e-3f, "G 像素");
            AssertClose(0.125f, HalfAt(loaded, "B", 4, 2), 1e-3f, "B 像素");
            AssertClose(0.5f, loaded.GetFloatAttribute("ldrExposureEv"), 1e-6f, "曝光元数据");
            AssertClose(240f, loaded.GetFloatAttribute("sdrWhiteNits"), 1e-6f, "白点元数据");
            AssertTrue(
                loaded.GetStringAttribute("captureTimestamp").StartsWith("20", StringComparison.Ordinal),
                "时间戳元数据缺失");
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static void TestNegativeCoordinateCrop()
    {
        var capture = CreateHdrMonitor(
            @"\\.\DISPLAY-1",
            -1920,
            -1080,
            8,
            4,
            308,
            (column, row) => (column + 1, row + 1, 0.5f));

        var image = RegionComposer.Compose([capture], new PixelRect(-1918, -1078, 3, 2));
        AssertEqual(3, image.Width, "裁切宽度");
        AssertEqual(2, image.Height, "裁切高度");

        AssertClose(3f, HalfAt(image, "R", 0, 0), 1e-3f, "负坐标 R[0,0]");
        AssertClose(5f, HalfAt(image, "R", 2, 0), 1e-3f, "负坐标 R[2,0]");
        AssertClose(3f, HalfAt(image, "G", 0, 0), 1e-3f, "负坐标 G[0,0]");
        AssertClose(4f, HalfAt(image, "G", 0, 1), 1e-3f, "负坐标 G[0,1]");
        AssertEqual(1, image.Regions.Count, "区域数量");
        AssertEqual(0, image.Regions[0].X, "区域局部 X");
        AssertClose(308f, image.Regions[0].SdrWhiteNits, 1e-3f, "区域白点");
    }

    private static void TestCrossMonitorStitching()
    {
        var left = CreateHdrMonitor(@"\\.\DISPLAYL", 0, 0, 4, 4, 288, (_, _) => (1f, 2f, 3f));
        var right = CreateHdrMonitor(@"\\.\DISPLAYR", 8, 0, 4, 4, 308, (_, _) => (11f, 12f, 13f));

        var image = RegionComposer.Compose([left, right], new PixelRect(2, 0, 8, 4));
        AssertEqual(8, image.Width, "跨屏宽度");

        AssertClose(1f, HalfAt(image, "R", 0, 0), 1e-3f, "左屏像素");
        AssertClose(1f, HalfAt(image, "R", 1, 3), 1e-3f, "左屏边界像素");
        AssertClose(0f, HalfAt(image, "R", 2, 0), 1e-3f, "间隙起始像素为黑");
        AssertClose(0f, HalfAt(image, "G", 4, 2), 1e-3f, "间隙内像素为黑");
        AssertClose(0f, HalfAt(image, "B", 5, 3), 1e-3f, "间隙末尾像素为黑");
        AssertClose(11f, HalfAt(image, "R", 6, 0), 1e-3f, "右屏像素");
        AssertClose(13f, HalfAt(image, "B", 7, 3), 1e-3f, "右屏末尾像素");

        AssertEqual(2, image.Regions.Count, "拼接区域数量");
        AssertEqual(0, image.Regions[0].X, "左区域起始");
        AssertEqual(6, image.Regions[1].X, "右区域起始");
        AssertClose(288f, image.Regions[0].SdrWhiteNits, 1e-3f, "左区域白点");
        AssertClose(308f, image.Regions[1].SdrWhiteNits, 1e-3f, "右区域白点");
    }

    private static void TestSdrWhitePointNormalization()
    {
        // 288 nit of SDR white equals 3.6 in scRGB, which is the same physical brightness as
        // 1.0 on an 80 nit reference display: both must produce identical LDR pixels.
        var display = new LinearImage(2, 1);
        SetPixel(display, 0, 3.6f, 3.6f, 3.6f);
        SetPixel(display, 1, 1.8f, 1.8f, 1.8f);
        display.Regions.Add(new WhitePointRegion(0, 0, 2, 1, @"\\.\DISPLAY2", true, 288));

        var bgra = LdrConverter.ToBgra(display, 0);
        AssertEqual(WhitePixel(), bgra[2], "288 nit 屏的 SDR 白应等于参考白");
        AssertEqual(HalfTone(0.5f), bgra[6], "288 nit 屏的中灰应等于参考显示器的 0.5");

        // On an SDR monitor the captured BGRA8 is already normalized, so the reported nit
        // value must not dim it.
        var sdrCapture = CreateSdrMonitor(@"\\.\DISPLAYS", 0, 0, 2, 1, 288, (byte)255, 0, 128);
        var sdrImage = RegionComposer.Compose([sdrCapture], new PixelRect(0, 0, 2, 1));
        AssertClose(1.0f, HalfAt(sdrImage, "R", 0, 0), 1e-3f, "SDR 白应为 1.0 scRGB");
        AssertClose(1.0f, HalfAt(sdrImage, "R", 1, 0), 1e-3f, "SDR 白应为 1.0 scRGB");

        var sdrBgra = LdrConverter.ToBgra(sdrImage, 0);
        AssertTrue(
            Math.Abs(sdrBgra[2] - WhitePixel()) <= 1,
            $"SDR 白应与 HDR 屏上的 SDR 白一致（{sdrBgra[2]} vs {WhitePixel()}）");
        AssertTrue(
            sdrBgra[1] < 64 && sdrBgra[1] < sdrBgra[0] && sdrBgra[0] < sdrBgra[2],
            $"亮度顺序应为 G < B < R，实际 {sdrBgra[1]}/{sdrBgra[0]}/{sdrBgra[2]}");

        // Exposure compensation of +1 EV doubles the linear value.
        var exposureImage = new LinearImage(1, 1);
        SetPixel(exposureImage, 0, 0.25f, 0.25f, 0.25f);
        exposureImage.Regions.Add(new WhitePointRegion(0, 0, 1, 1, @"\\.\DISPLAY2", true, 80));
        var brightened = LdrConverter.ToBgra(exposureImage, 1);

        var doubledImage = new LinearImage(1, 1);
        SetPixel(doubledImage, 0, 0.5f, 0.5f, 0.5f);
        doubledImage.Regions.Add(new WhitePointRegion(0, 0, 1, 1, @"\\.\DISPLAY2", true, 80));
        AssertEqual(LdrConverter.ToBgra(doubledImage, 0)[2], brightened[2], "+1 EV 应让 0.25 变为 0.5");
    }

    private static void TestNeutralTonemap()
    {
        var white = NeutralTonemapper.Map(1f, 1f, 1f);
        // Reference behaviour: the curve starts compressing at 0.76, so SDR white keeps a
        // little headroom for highlights instead of being clipped to display white.
        // 0.76 knee plus the 0.04 offset subtraction: SDR white lands at ~0.869 (sRGB ~240),
        // which leaves the top of the range for highlights above SDR white.
        AssertClose(0.86909f, white.Red, 1e-3f, "参考曲线应把 SDR 白压缩到约 0.869");
        AssertClose(white.Red, white.Green, 1e-4f, "中性输入应保持中性");

        // Below the knee the reference curve only subtracts the 0.04 offset: no compression.
        var belowKnee = NeutralTonemapper.Map(0.5f, 0.25f, 0.1f);
        AssertClose(0.46f, belowKnee.Red, 1e-4f, "膝点以下只应扣除 0.04 偏移");
        AssertClose(0.21f, belowKnee.Green, 1e-4f, "膝点以下只应扣除 0.04 偏移");

        // Near black the offset shrinks with the pixel value so shadows are not crushed.
        var shadow = NeutralTonemapper.Map(0.01f, 0.01f, 0.01f);
        AssertTrue(shadow.Red > 0f && shadow.Red < 0.01f, $"暗部不应被压到 0：{shadow.Red}");

        var highlight = NeutralTonemapper.Map(8f, 8f, 8f);
        AssertTrue(highlight.Red <= 1f && highlight.Red > 0.9f, $"高光应压缩到 1 以内：{highlight.Red}");
        AssertTrue(
            highlight.Red > NeutralTonemapper.Map(4f, 4f, 4f).Red,
            "更亮的高光应保持单调递增");

        var saturated = NeutralTonemapper.Map(20f, 0f, 0f);
        AssertTrue(saturated.Red <= 1f, "饱和高光不得超过 1.0");
        AssertTrue(saturated.Green > 0f, "饱和高光应产生去饱和");
        AssertClose(saturated.Green, saturated.Blue, 1e-4f, "去饱和应保持中性");

        var negative = NeutralTonemapper.Map(-0.5f, -0.1f, -2f);
        AssertTrue(
            float.IsFinite(negative.Red) && float.IsFinite(negative.Green) && float.IsFinite(negative.Blue),
            "负值输入不应产生 NaN");
    }

    private static void TestFileNaming()
    {
        var capture = CreateHdrMonitor(@"\\.\DISPLAY2", 0, 0, 4, 4, 288, (_, _) => (1f, 1f, 1f));
        var name = CaptureFileNaming.BuildFileName(
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, 678, TimeSpan.Zero),
            new PixelRect(1, 1, 2, 2),
            [capture]);
        AssertEqual("HDR_20260102_030405_678_DISPLAY2.exr", name, "文件名格式");

        var spanning = CaptureFileNaming.BuildFileName(
            DateTimeOffset.Now,
            new PixelRect(0, 0, 2, 2),
            [capture]);
        AssertTrue(
            spanning.EndsWith("_DISPLAY2.exr", StringComparison.Ordinal),
            "单屏区域内应使用显示器名称");

        var multi = CaptureFileNaming.BuildFileName(
            DateTimeOffset.Now,
            new PixelRect(-10, 0, 100, 100),
            [capture]);
        AssertTrue(multi.EndsWith("_span.exr", StringComparison.Ordinal), "跨屏文件名应使用 span");

        var directory = Path.Combine(Path.GetTempPath(), $"hdrcapture-naming-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            var first = CaptureFileNaming.EnsureUniquePath(directory, "HDR_test.exr");
            File.WriteAllBytes(first, [0]);
            var second = CaptureFileNaming.EnsureUniquePath(directory, "HDR_test.exr");
            AssertEqual("HDR_test_1.exr", Path.GetFileName(second), "重名文件应追加序号");
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static void TestSettingsRecovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hdrcapture-settings-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            var store = new SettingsStore(directory);

            var defaults = store.Load();
            AssertEqual(AppSettings.CurrentVersion, defaults.Version, "默认版本");
            AssertTrue(defaults.EffectiveSaveDirectory.EndsWith("picture", StringComparison.OrdinalIgnoreCase), "默认目录应为 picture");
            AssertTrue(!defaults.SaveExr, "默认不应保存 EXR");
            AssertEqual(PreviewQuality.Low, defaults.PreviewQuality, "默认预览画质");

            File.WriteAllText(
                store.FilePath,
                """
                {
                  "Version": 1,
                  "Hotkey": { "Modifiers": 3, "VirtualKey": 80 },
                  "SaveDirectory": "",
                  "ExposureEv": 0,
                  "StartWithWindows": false,
                  "IncludeCursor": true
                }
                """);
            var migrated = store.Load();
            AssertTrue(!migrated.SaveExr, "旧配置缺失时不应默认保存 EXR");
            AssertEqual(PreviewQuality.Low, migrated.PreviewQuality, "旧配置缺失时应使用低画质");
            AssertEqual(AppSettings.CurrentVersion, migrated.Version, "旧配置应迁移到当前版本");
            AssertTrue(migrated.Denoise is not null, "旧配置应补齐降噪默认值");
            AssertTrue(migrated.Portrait is not null, "旧配置应补齐人像默认值");
            AssertTrue(migrated.ComfyUi is not null, "旧配置应补齐 ComfyUI 默认值");

            defaults.ExposureEv = 9;
            defaults.SaveDirectory = "%TEMP%";
            defaults.SaveExr = true;
            defaults.PreviewQuality = (PreviewQuality)99;
            defaults.Normalize();
            AssertClose(5.0, defaults.ExposureEv, 1e-6, "曝光应被限制在 ±5 EV");
            AssertTrue(Path.IsPathFullyQualified(defaults.SaveDirectory), "环境变量应展开为绝对路径");
            AssertEqual(PreviewQuality.Low, defaults.PreviewQuality, "非法预览画质应恢复为低画质");

            store.Save(defaults);
            var reloaded = store.Load();
            AssertClose(defaults.ExposureEv, reloaded.ExposureEv, 1e-6, "配置应能保存并重新读取");
            AssertTrue(reloaded.SaveExr, "保存 EXR 选项应能持久化");
            AssertEqual(PreviewQuality.Low, reloaded.PreviewQuality, "预览画质应能持久化");

            defaults.ComfyUi.BaseUrl = "http://192.168.1.2:8188";
            defaults.ComfyUi.Port = 80;
            defaults.Denoise.Strength = 9;
            defaults.Portrait.SmoothSkin = -1;
            defaults.Normalize();
            AssertEqual("http://127.0.0.1:8188", defaults.ComfyUi.BaseUrl, "非回环地址应被拒绝");
            AssertEqual(8188, defaults.ComfyUi.Port, "非法端口应恢复默认");
            AssertClose(1.0, defaults.Denoise.Strength, 1e-6, "降噪强度应限制在 0..1");
            AssertClose(0.0, defaults.Portrait.SmoothSkin, 1e-6, "磨皮强度应限制在 0..1");

            File.WriteAllText(store.FilePath, "{ this is not json");
            var recovered = store.Load();
            AssertEqual(AppSettings.CurrentVersion, recovered.Version, "损坏配置应恢复默认值");
            AssertClose(0.0, recovered.ExposureEv, 1e-6, "损坏配置应恢复默认曝光");
            AssertTrue(
                Directory.GetFiles(directory, "*.bad").Length == 1,
                "损坏的配置应被备份为 .bad");
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static void TestPreviewQuality()
    {
        var full = PreviewRenderer.GetPreviewDimensions(7680, 4320, PreviewQuality.Full);
        var half = PreviewRenderer.GetPreviewDimensions(7680, 4320, PreviewQuality.Half);
        var low = PreviewRenderer.GetPreviewDimensions(7680, 4320, PreviewQuality.Low);

        AssertEqual(7680, full.Width, "全分辨率预览宽度");
        AssertEqual(4320, full.Height, "全分辨率预览高度");
        AssertEqual(3840, half.Width, "半分辨率预览宽度");
        AssertEqual(2160, half.Height, "半分辨率预览高度");
        AssertEqual(1920, low.Width, "低画质预览宽度");
        AssertEqual(1080, low.Height, "低画质预览高度");

        var fourK = PreviewRenderer.GetPreviewDimensions(3840, 2160, PreviewQuality.Low);
        AssertEqual(1920, fourK.Width, "4K 低画质预览宽度");
        AssertEqual(1080, fourK.Height, "4K 低画质预览高度");

        var capture = CreateSdrMonitor(
            @"\\.\DISPLAYP",
            0,
            0,
            7,
            5,
            240,
            128,
            96,
            64);
        var fullPreview = PreviewRenderer.Create(capture, PreviewQuality.Full);
        var halfPreview = PreviewRenderer.Create(capture, PreviewQuality.Half);
        AssertEqual(7, fullPreview.PixelWidth, "全分辨率预览实际宽度");
        AssertEqual(5, fullPreview.PixelHeight, "全分辨率预览实际高度");
        AssertEqual(4, halfPreview.PixelWidth, "半分辨率预览实际宽度");
        AssertEqual(3, halfPreview.PixelHeight, "半分辨率预览实际高度");
    }

    private static void TestExrSaveSwitch()
    {
        var defaults = new AppSettings();
        AssertTrue(!CaptureWorkflow.ShouldSaveExr(defaults), "默认设置不应保存 EXR");
        AssertTrue(
            CaptureWorkflow.ShouldSaveExr(defaults, "explicit.exr"),
            "显式输出路径应强制保存 EXR");

        defaults.SaveExr = true;
        AssertTrue(CaptureWorkflow.ShouldSaveExr(defaults), "勾选设置后应保存 EXR");
    }

    private static void TestSaveDirectoryFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hdrcapture-save-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            var filePath = Path.Combine(directory, "not-a-directory");
            File.WriteAllBytes(filePath, [0]);

            var failed = false;
            try
            {
                CaptureWorkflow.EnsureSaveDirectory(
                    new AppSettings { SaveDirectory = Path.Combine(filePath, "picture") });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed = true;
            }

            AssertTrue(failed, "路径不可写时应抛出异常，不能静默回退");
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static void TestLastCaptureStore()
    {
        var firstImage = new LinearImage(2, 1);
        SetPixel(firstImage, 0, 1, 1, 1);
        SetPixel(firstImage, 1, 1, 1, 1);
        var first = new CapturedImageSnapshot(
            firstImage,
            new PixelRect(0, 0, 2, 1),
            DateTimeOffset.Now,
            "test",
            240);
        var secondImage = new LinearImage(1, 1);
        SetPixel(secondImage, 0, 2, 2, 2);
        var second = new CapturedImageSnapshot(
            secondImage,
            new PixelRect(10, 10, 1, 1),
            DateTimeOffset.Now,
            "test",
            308);

        using var store = new LastCaptureStore();
        store.Store(first);
        store.Store(second);
        AssertTrue(ReferenceEquals(second, store.Current), "应只保留最新截图");
        var disposed = false;
        try
        {
            _ = first.Image;
        }
        catch (ObjectDisposedException)
        {
            disposed = true;
        }

        AssertTrue(disposed, "被替换的 HDR 图像应被释放");
    }

    private static void TestFastHdrDenoise()
    {
        var image = new LinearImage(8, 4);
        for (var index = 0; index < image.Red.Length; index++)
        {
            var value = (index & 1) == 0 ? 0.25f : 0.75f;
            SetPixel(image, index, value, value * 0.8f, value * 0.6f);
        }

        image.Regions.Add(new WhitePointRegion(0, 0, 8, 4, @"\\.\DISPLAYT", true, 240));
        var result = HdrDenoiser.DenoiseFast(image, 0.8, 0.4);
        AssertTrue(!ReferenceEquals(image, result.Image), "快速降噪应生成新图像");
        foreach (var value in result.Image.Red)
        {
            AssertTrue(float.IsFinite(RegionComposer.FromHalfBits(value)), "快速降噪产生了非有限值");
        }
    }

    private static void TestOidnHdrDenoise()
    {
        AssertTrue(OidnRuntime.IsAvailable, $"OIDN 不可用：{OidnRuntime.Error}");
        var image = new LinearImage(32, 24);
        for (var row = 0; row < image.Height; row++)
        {
            for (var column = 0; column < image.Width; column++)
            {
                var noise = (((row * 17) + (column * 13)) % 7) * 0.015f;
                var value = 0.18f + (column / 64f) + noise;
                SetPixel(image, (row * image.Width) + column, value, value * 0.9f, value * 0.8f);
            }
        }

        image.Regions.Add(new WhitePointRegion(0, 0, image.Width, image.Height, @"\\.\DISPLAYT", true, 240));
        var result = HdrDenoiser.DenoiseFinal(
            image,
            1.0,
            0.0,
            useOidn: true,
            CancellationToken.None);
        AssertTrue(result.UsedOidn, result.Warning ?? "没有使用 OIDN");
        foreach (var value in result.Image.Red)
        {
            AssertTrue(float.IsFinite(RegionComposer.FromHalfBits(value)), "OIDN 返回了非有限值");
        }
    }

    private static void TestComfyUiWorkflow()
    {
        var workflow = ComfyUiWorkflowBuilder.BuildPortraitWorkflow(
            new PortraitWorkflowParameters(
                "model.safetensors",
                "HDRCapture_test.png",
                123,
                0.35,
                10,
                18,
                0.6,
                "HDRCapture_final_test",
                "HDRCapture_mask_test"));
        AssertEqual("CheckpointLoaderSimple", workflow["1"]?["class_type"]?.GetValue<string>(), "检查点节点");
        AssertEqual("FaceDetailer", workflow["20"]?["class_type"]?.GetValue<string>(), "FaceDetailer 节点");
        AssertEqual("ImageBlend", workflow["30"]?["class_type"]?.GetValue<string>(), "融合节点");
        AssertEqual("SaveImage", workflow["31"]?["class_type"]?.GetValue<string>(), "输出节点");
        AssertEqual(
            "model.safetensors",
            workflow["1"]?["inputs"]?["ckpt_name"]?.GetValue<string>(),
            "检查点占位符");
        AssertEqual(
            123L,
            workflow["20"]?["inputs"]?["seed"]?.GetValue<long>(),
            "随机种子占位符");
        AssertEqual(
            "HDRCapture_test.png",
            workflow["2"]?["inputs"]?["image"]?.GetValue<string>(),
            "输入图片占位符");

        var withIpAdapter = ComfyUiWorkflowBuilder.BuildPortraitWorkflow(
            new PortraitWorkflowParameters(
                "model.safetensors",
                "HDRCapture_test.png",
                456,
                0.35,
                10,
                18,
                0.6,
                "HDRCapture_final_test",
                "HDRCapture_mask_test",
                UseIpAdapter: true,
                IpAdapterFile: "ip-adapter-plus-face_sdxl_vit-h.safetensors",
                ClipVisionFile: "clip_vision_h.safetensors"));
        AssertEqual(
            "IPAdapterModelLoader",
            withIpAdapter["40"]?["class_type"]?.GetValue<string>(),
            "IP-Adapter 加载节点");
        AssertEqual(
            "42",
            withIpAdapter["20"]?["inputs"]?["model"]?[0]?.GetValue<string>(),
            "FaceDetailer 应使用 IP-Adapter 模型");
    }

    private static void TestComfyUiWorkflowTemplate()
    {
        var path = WorkflowTemplateStore.EnsurePortraitTemplate();
        AssertTrue(File.Exists(path), "本地工作流模板未生成");
        var template = WorkflowTemplateStore.LoadPortraitTemplate();
        AssertEqual(
            "FaceDetailer",
            template["20"]?["class_type"]?.GetValue<string>(),
            "模板中的 FaceDetailer 节点");
    }

    private static void TestComfyUiErrorText()
    {
        var translated = ComfyUiErrorText.Translate("clip input is invalid");
        AssertTrue(
            translated.Contains("SD 或 SDXL", StringComparison.Ordinal),
            "CLIP 错误应给出检查点类型提示");
        AssertEqual(
            "out of memory",
            ComfyUiErrorText.Translate("out of memory"),
            "未知错误应原样保留");
    }

    private static void TestComfyUiPathValidation()
    {
        var invalid = ComfyUiPathValidator.Validate(Path.Combine(
            Path.GetTempPath(),
            $"hdrcapture-no-comfy-{Guid.NewGuid():N}"));
        AssertTrue(!invalid.IsValid, "不存在的 ComfyUI 路径不应通过检测");
        AssertTrue(invalid.Missing.Count > 0, "无效 ComfyUI 路径应说明缺失项");

        const string expectedRoot = @"D:\AI\ComfyUI";
        if (Directory.Exists(expectedRoot))
        {
            var valid = ComfyUiPathValidator.Validate(expectedRoot);
            AssertTrue(
                valid.IsValid,
                "本机 ComfyUI 检测失败：" + string.Join("；", valid.Missing));
            AssertTrue(
                File.Exists(Path.Combine(
                    valid.Installation!.ComfyUiDirectory,
                    "custom_nodes",
                    "ComfyUI-Impact-Pack",
                    "modules",
                    "impact",
                    "impact_pack.py")),
                "Impact Pack 主模块缺失");
        }
    }

    private static void TestComfyUiOptionalComponents()
    {
        const string expectedRoot = @"D:\AI\ComfyUI";
        if (!Directory.Exists(expectedRoot))
        {
            return;
        }

        var validation = ComfyUiPathValidator.Validate(expectedRoot);
        AssertTrue(validation.IsValid, "本机 ComfyUI 路径应有效");
        var status = ComfyUiOptionalComponents.Inspect(validation.Installation!);
        AssertTrue(status.NodeAvailable, "IP-Adapter Plus 节点未安装");
        AssertTrue(
            !string.IsNullOrWhiteSpace(status.ClipVisionPath),
            "CLIP-Vision 模型未安装");
    }

    private static CapturedMonitor CreateHdrMonitor(
        string device,
        int x,
        int y,
        int width,
        int height,
        double whiteNits,
        Func<int, int, (float Red, float Green, float Blue)> pixel)
    {
        var bytes = new byte[width * height * 8];
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                var (red, green, blue) = pixel(column, row);
                var index = ((row * width) + column) * 8;
                WriteHalf(bytes, index, red);
                WriteHalf(bytes, index + 2, green);
                WriteHalf(bytes, index + 4, blue);
                WriteHalf(bytes, index + 6, 1f);
            }
        }

        return new CapturedMonitor(
            new MonitorInfo(1, device, x, y, width, height, 96, true, whiteNits),
            CapturePixelFormat.Rgba16Float,
            width,
            height,
            bytes);
    }

    private static CapturedMonitor CreateSdrMonitor(
        string device,
        int x,
        int y,
        int width,
        int height,
        double whiteNits,
        byte red,
        byte green,
        byte blue)
    {
        var bytes = new byte[width * height * 4];
        for (var index = 0; index < bytes.Length; index += 4)
        {
            bytes[index] = blue;
            bytes[index + 1] = green;
            bytes[index + 2] = red;
            bytes[index + 3] = 255;
        }

        return new CapturedMonitor(
            new MonitorInfo(2, device, x, y, width, height, 96, false, whiteNits),
            CapturePixelFormat.Bgra8,
            width,
            height,
            bytes);
    }

    private static void WriteHalf(byte[] buffer, int offset, float value)
    {
        var bits = RegionComposer.ToHalfBits(value);
        buffer[offset] = (byte)(bits & 0xFF);
        buffer[offset + 1] = (byte)(bits >> 8);
    }

    private static float HalfAt(ExrImage image, string channel, int x, int y) =>
        (float)BitConverter.UInt16BitsToHalf(image.Planes[channel][(y * image.Width) + x]);

    private static byte WhitePixel() =>
        LdrConverter.EncodeSrgb(NeutralTonemapper.Map(1f, 1f, 1f).Red);

    private static byte HalfTone(float value) =>
        LdrConverter.EncodeSrgb(NeutralTonemapper.Map(value, value, value).Red);

    private static float HalfAt(LinearImage image, string channel, int x, int y)
    {
        var plane = channel switch
        {
            "R" => image.Red,
            "G" => image.Green,
            _ => image.Blue
        };
        return (float)BitConverter.UInt16BitsToHalf(plane[(y * image.Width) + x]);
    }

    private static void SetPixel(LinearImage image, int index, float red, float green, float blue)
    {
        image.Red[index] = RegionComposer.ToHalfBits(red);
        image.Green[index] = RegionComposer.ToHalfBits(green);
        image.Blue[index] = RegionComposer.ToHalfBits(blue);
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}：期望 {expected}，实际 {actual}");
        }
    }

    private static void AssertClose(float expected, float actual, float tolerance, string message)
    {
        if (MathF.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException($"{message}：期望 {expected}，实际 {actual}");
        }
    }

    private static void AssertClose(double expected, double actual, double tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException($"{message}：期望 {expected}，实际 {actual}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
