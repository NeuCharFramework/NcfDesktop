using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NcfDesktopApp.GUI.Models;

namespace NcfDesktopApp.GUI.Views.Controls;

/// <summary>
/// 浮窗宠物圆框外的轻量音频发光。使用少量半透明描边模拟外发光，
/// 不启用实时模糊、阴影或额外动画计时器。
/// </summary>
public sealed class AudioGlowRingView : Control
{
    private const int RingCount = 4;
    private static readonly Color QuietColor = Color.Parse("#22D3EE");
    private static readonly Color LoudColor = Color.Parse("#A855F7");

    public static readonly StyledProperty<AudioVisualizationMode> ModeProperty =
        AvaloniaProperty.Register<AudioGlowRingView, AudioVisualizationMode>(nameof(Mode));
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<AudioGlowRingView, double>(nameof(Level));
    public static readonly StyledProperty<double[]> BandsProperty =
        AvaloniaProperty.Register<AudioGlowRingView, double[]>(nameof(Bands), new double[12]);
    public static readonly StyledProperty<double> InnerDiameterProperty =
        AvaloniaProperty.Register<AudioGlowRingView, double>(nameof(InnerDiameter), 64d);

    private double _smoothedLevel;
    private double _smoothedFrequencyEnergy;

    static AudioGlowRingView() => AffectsRender<AudioGlowRingView>(
        ModeProperty,
        LevelProperty,
        BandsProperty,
        InnerDiameterProperty);

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

    public double InnerDiameter
    {
        get => GetValue(InnerDiameterProperty);
        set => SetValue(InnerDiameterProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != LevelProperty && change.Property != BandsProperty && change.Property != ModeProperty)
        {
            return;
        }

        if (Mode == AudioVisualizationMode.None)
        {
            _smoothedLevel = 0;
            _smoothedFrequencyEnergy = 0;
            return;
        }

        var targetLevel = Math.Clamp(Level, 0, 1);
        var targetFrequencyEnergy = GetFrequencyEnergy(Bands);
        // 音量上升响应更快、回落稍慢，避免颜色和半径抖动；不引入独立计时器。
        _smoothedLevel = Smooth(_smoothedLevel, targetLevel, targetLevel >= _smoothedLevel ? .42 : .24);
        _smoothedFrequencyEnergy = Smooth(
            _smoothedFrequencyEnergy,
            targetFrequencyEnergy,
            targetFrequencyEnergy >= _smoothedFrequencyEnergy ? .36 : .2);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Mode == AudioVisualizationMode.None || Bounds.Width < 2 || Bounds.Height < 2)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var innerRadius = Math.Max(1, InnerDiameter / 2);
        var availableSpread = Math.Max(1, Math.Min(Bounds.Width, Bounds.Height) / 2 - innerRadius - 1);
        var response = Math.Clamp(_smoothedLevel * .72 + _smoothedFrequencyEnergy * .28, 0, 1);
        var spread = Math.Min(availableSpread, 2.2 + availableSpread * response);
        var color = Mix(QuietColor, LoudColor, _smoothedLevel);
        var coreAlpha = 55 + 110 * response;

        for (var index = 0; index < RingCount; index++)
        {
            var position = (index + 1d) / RingCount;
            var radius = innerRadius + .8 + spread * position;
            var alpha = (byte)Math.Clamp(coreAlpha * (1 - position * .68), 18, 150);
            var thickness = Math.Max(.8, 2.1 - position * .85);
            context.DrawEllipse(
                null,
                new Pen(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)), thickness),
                center,
                radius,
                radius);
        }
    }

    private static double GetFrequencyEnergy(double[]? bands)
    {
        if (bands == null || bands.Length == 0)
        {
            return 0;
        }

        double weightedEnergy = 0;
        double totalWeight = 0;
        for (var index = 0; index < bands.Length; index++)
        {
            var weight = 1 + index / (double)Math.Max(1, bands.Length - 1) * .35;
            weightedEnergy += Math.Clamp(bands[index], 0, 1) * weight;
            totalWeight += weight;
        }

        return totalWeight <= 0 ? 0 : Math.Clamp(weightedEnergy / totalWeight, 0, 1);
    }

    private static double Smooth(double current, double target, double factor) =>
        current + (target - current) * factor;

    private static Color Mix(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }
}
