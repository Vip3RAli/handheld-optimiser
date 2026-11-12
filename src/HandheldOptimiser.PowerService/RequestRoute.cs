namespace HandheldOptimiser.PowerService;

/// <summary>Answers one request that is not the engine's own.</summary>
/// <param name="request">What was asked.</param>
/// <param name="last">What the engine last said, which the answer is built on.</param>
/// <param name="error">The service's own standing error, such as a power limit it cannot set, or null.</param>
internal delegate AutoTdpStatus RequestHandler(AutoTdpRequest request, AutoTdpStatus last, string? error);

/// <summary>Which part of the service answers a request. See AutoTdpService.Routes.</summary>
/// <param name="Answer">The part's own handler.</param>
/// <param name="KeepsAwake">Whether the request counts as the service being used, so it does not end by itself yet.</param>
internal sealed record RequestRoute(RequestHandler Answer, bool KeepsAwake = false);
