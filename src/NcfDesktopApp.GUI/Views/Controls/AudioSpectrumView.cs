/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：AudioSpectrumView.cs
    文件功能描述：使用实际 PCM 能量和频段绘制语音输入/朗读声波

    创建标识：Senparc - 20260803

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 绘制本地音频频谱可视化

----------------------------------------------------------------*/

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NcfDesktopApp.GUI.Models;

namespace NcfDesktopApp.GUI.Views.Controls;

public sealed class AudioSpectrumView : Control
{
    public static readonly StyledProperty<AudioVisualizationMode> ModeProperty =
        AvaloniaProperty.Register<AudioSpectrumView, AudioVisualizationMode>(nameof(Mode));
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<AudioSpectrumView, double>(nameof(Level));
    public static readonly StyledProperty<double[]> BandsProperty =
        AvaloniaProperty.Register<AudioSpectrumView, double[]>(nameof(Bands), new double[12]);

    private readonly DispatcherTimer _timer;
    private double _phase;
    private bool _isAttached;

    static AudioSpectrumView() => AffectsRender<AudioSpectrumView>(ModeProperty, LevelProperty, BandsProperty);

    public AudioSpectrumView()
    {
        MinWidth = 110;
        MinHeight = 30;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) =>
        {
            _phase = (_phase + .035) % 1;
            InvalidateVisual();
        };
    }

    public AudioVisualizationMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public double[] Bands
    {
        get => GetValue(BandsProperty);
        set => SetValue(BandsProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        UpdateAnimationTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModeProperty || change.Property == IsVisibleProperty)
        {
            UpdateAnimationTimer();
        }
    }

    private void UpdateAnimationTimer()
    {
        // 频谱只在实际录音或朗读并且控件可见时才需要扫描动画。
        // 空闲状态不再以 20 FPS 无效重绘整个控件。
        if (_isAttached && IsVisible && Mode != AudioVisualizationMode.None)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Mode == AudioVisualizationMode.None || Bounds.Width < 2 || Bounds.Height < 2)
        {
            return;
        }

        var isSpeaking = Mode == AudioVisualizationMode.Speaking;
        var primary = Color.Parse(isSpeaking ? "#8B5CF6" : "#06B6D4");
        var highlight = Color.Parse(isSpeaking ? "#22D3EE" : "#38BDF8");
        var level = Math.Clamp(Level, 0, 1);
        var rect = new Rect(0, 0, Bounds.Width, Bounds.Height);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(20, primary.R, primary.G, primary.B)),
            new Pen(new SolidColorBrush(Color.FromArgb(70, primary.R, primary.G, primary.B)), 1), rect, 6, 6);

        var centerY = Bounds.Height / 2;
        context.DrawLine(
            new Pen(new SolidColorBrush(Color.FromArgb(75, primary.R, primary.G, primary.B)), 1),
            new Point(8, centerY),
            new Point(Bounds.Width - 8, centerY));

        var bands = Bands ?? Array.Empty<double>();
        var barCount = Math.Max(12, bands.Length * 2);
        var available = Math.Max(1, Bounds.Width - 18);
        var stride = available / barCount;
        var barWidth = Math.Max(1.2, stride * .48);
        for (var index = 0; index < barCount; index++)
        {
            var mirroredIndex = index < barCount / 2 ? index : barCount - index - 1;
            var band = bands.Length == 0 ? 0 : bands[mirroredIndex % bands.Length];
            var normalized = Math.Clamp(band, 0, 1);
            var height = 2 + normalized * Math.Max(2, Bounds.Height * .38);
            var x = 9 + index * stride + (stride - barWidth) / 2;
            var alpha = (byte)(90 + normalized * 165);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, highlight.R, highlight.G, highlight.B));
            context.DrawRectangle(brush, null,
                new Rect(x, centerY - height, barWidth, height * 2), barWidth / 2, barWidth / 2);
        }

        var scanX = 8 + (Bounds.Width - 16) * _phase;
        context.DrawLine(
            new Pen(new SolidColorBrush(Color.FromArgb((byte)(35 + level * 85), highlight.R, highlight.G, highlight.B)), 1),
            new Point(scanX, 4),
            new Point(scanX, Bounds.Height - 4));
    }
}
