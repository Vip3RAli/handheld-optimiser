using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace HandheldOptimiser.HomeLauncher.Library;

/// <summary>
/// Draws an Old School card turned a little away from the player, in true perspective, and darker the
/// further back it is: the Console space look. The work is done on the GPU by Shaders\CardTilt.ps, whose
/// source is CardTilt.fx beside it. WPF's 2D transforms keep a card's edges parallel, so they cannot draw
/// its far edge shorter than its near one.
/// </summary>
internal sealed class CardTilt : ShaderEffect
{
    // Loaded once and shared: every card's effect runs the same program with its own numbers.
    private static readonly PixelShader Program = MakeProgram();

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(CardTilt), 0);

    public static readonly DependencyProperty TiltProperty = DependencyProperty.Register(
        nameof(Tilt), typeof(double), typeof(CardTilt), new UIPropertyMetadata(0d, PixelShaderConstantCallback(0)));

    public static readonly DependencyProperty DimProperty = DependencyProperty.Register(
        nameof(Dim), typeof(double), typeof(CardTilt), new UIPropertyMetadata(1d, PixelShaderConstantCallback(1)));

    public static readonly DependencyProperty CardLeftProperty = DependencyProperty.Register(
        nameof(CardLeft), typeof(double), typeof(CardTilt), new UIPropertyMetadata(0d, PixelShaderConstantCallback(2)));

    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(
        nameof(CardWidth), typeof(double), typeof(CardTilt), new UIPropertyMetadata(1d, PixelShaderConstantCallback(3)));

    public static readonly DependencyProperty CardMiddleProperty = DependencyProperty.Register(
        nameof(CardMiddle), typeof(double), typeof(CardTilt), new UIPropertyMetadata(0.5d, PixelShaderConstantCallback(4)));

    public CardTilt()
    {
        PixelShader = Program;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(TiltProperty);
        UpdateShaderValue(DimProperty);
        UpdateShaderValue(CardLeftProperty);
        UpdateShaderValue(CardWidthProperty);
        UpdateShaderValue(CardMiddleProperty);
    }

    /// <summary>The card the effect is drawn from.</summary>
    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    /// <summary>How much further away the far edge is than the near one: 0 is flat.</summary>
    public double Tilt
    {
        get => (double)GetValue(TiltProperty);
        set => SetValue(TiltProperty, value);
    }

    /// <summary>How bright the card is drawn, 1 for as it is.</summary>
    public double Dim
    {
        get => (double)GetValue(DimProperty);
        set => SetValue(DimProperty, value);
    }

    /// <summary>The card's left edge, as a part of the width the effect is given.</summary>
    public double CardLeft
    {
        get => (double)GetValue(CardLeftProperty);
        set => SetValue(CardLeftProperty, value);
    }

    /// <summary>The card's width, as a part of the width the effect is given.</summary>
    public double CardWidth
    {
        get => (double)GetValue(CardWidthProperty);
        set => SetValue(CardWidthProperty, value);
    }

    /// <summary>The line through the card's middle, as a part of the height the effect is given.</summary>
    public double CardMiddle
    {
        get => (double)GetValue(CardMiddleProperty);
        set => SetValue(CardMiddleProperty, value);
    }

    private static PixelShader MakeProgram()
    {
        var program = new PixelShader
        {
            UriSource = new Uri("pack://application:,,,/HandheldOptimiser.HomeLauncher;component/Library/Shaders/CardTilt.ps")
        };
        program.Freeze();
        return program;
    }
}
