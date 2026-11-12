namespace HandheldOptimiser.PowerService;

/// <summary>
/// The requests about the Ally's controller, from the widget or the library alike: both are the player.
/// There is only a host while the app stands in for Armoury Crate, so each of these is handed the one
/// there is, or null, and says what to turn on when there is none.
/// </summary>
internal sealed partial class AllyControlsHost
{
    private const string ButtonsOff =
        "Turn on Replace ASUS Services with Handheld Library and widget in Handheld Optimiser to use these buttons.";

    private const string LightsOff =
        "Turn on Replace ASUS Services with Handheld Library and widget in Handheld Optimiser to use the stick lights.";

    /// <summary>
    /// A new controller mode. Answered with the last status, its Error the notice when the mode could not
    /// be sent now, or the service's own error otherwise.
    /// </summary>
    public static AutoTdpStatus AnswerMode(AllyControlsHost? controls, AutoTdpRequest request, AutoTdpStatus last, string? error)
    {
        if (controls is null)
        {
            return last with { Error = ButtonsOff };
        }

        // Temporary with no mode: Game Bar has closed, so the hold ends and the saved mode comes back.
        if (request is { Temporary: true, ControllerMode: null })
        {
            return last with { Error = controls.Release() ?? error };
        }

        if (AllyCommands.ParseMode(request.ControllerMode) is not { } mode)
        {
            return last with { Error = "That controller mode is not supported." };
        }

        // The service's own standing error, such as a power limit it cannot set, is kept when the mode went
        // through, so the widget still shows it.
        return last with { Error = controls.SetMode(mode, request.Temporary) ?? error };
    }

    /// <summary>A new back paddle action.</summary>
    public static AutoTdpStatus AnswerPaddle(AllyControlsHost? controls, AutoTdpRequest request, AutoTdpStatus last, string? error) =>
        controls is null
            ? last with { Error = ButtonsOff }
            : last with { Error = controls.SetPaddleAction(request.PaddleAction) ?? error };

    /// <summary>A new thumbstick light ring mode or colour.</summary>
    public static AutoTdpStatus AnswerLighting(AllyControlsHost? controls, AutoTdpRequest request, AutoTdpStatus last, string? error)
    {
        if (controls is null)
        {
            return last with { Error = LightsOff };
        }

        var problem = request.Command == AutoTdpProtocol.SetLightingColour
            ? controls.SetColour(request.LightingColour)
            : controls.SetLighting(request.ControllerLighting);
        return last with { Error = problem ?? error };
    }

    /// <summary>The status with the controller's state, while replacing Armoury Crate. Otherwise as it is.</summary>
    public static AutoTdpStatus AddTo(AllyControlsHost? controls, AutoTdpStatus status) => controls is null
        ? status
        : status with
        {
            Replacing = true,
            ControllerConnected = controls.Connected,
            ControllerMode = controls.Mode is { } mode ? AllyCommands.ModeName(mode) : null,
            PaddleAction = controls.PaddleAction,
            ControllerLighting = controls.Lighting,
            LightingColour = controls.Colour
        };
}
