using WindowsGSM.Contracts;
using WindowsGSM.Engine.Services;

namespace WindowsGSM.Agent.Api;

/// <summary>Uniform responses: errors are always <see cref="ApiError"/> with a stable code.</summary>
public static class ApiResults
{
    public static IResult Error(int status, string code, string message, IReadOnlyList<string>? details = null) =>
        Results.Json(new ApiError(message, code, details), statusCode: status);

    public static IResult BadRequest(string message, IReadOnlyList<string>? details = null) => Error(400, "bad_request", message, details);
    public static IResult Unauthorized() => Error(401, "unauthorized", "Sign in first.");
    public static IResult Forbidden(string message = "You don't have permission to do that.") => Error(403, "forbidden", message);
    public static IResult NotFound(string message = "Not found.") => Error(404, "not_found", message);
    public static IResult Conflict(string message) => Error(409, "conflict", message);

    /// <summary>202 with the job — or 409 with the engine's reason (busy, wrong state…).</summary>
    public static IResult FromRequest(OperationRequest request, AgentContext ctx) =>
        request.Accepted && request.Job != null
            ? Results.Json(new JobAccepted(request.Job.Id, ctx.ToDto(request.Job.Snapshot())), statusCode: 202)
            : Error(409, "rejected", request.Error ?? "The server can't do that right now.");

    public static IResult FromFileProblem(FileOperationException ex) => ex.Problem switch
    {
        FileProblem.NotFound => NotFound(ex.Message),
        FileProblem.Conflict => Conflict(ex.Message),
        FileProblem.TooLarge => Error(413, "too_large", ex.Message),
        _ => BadRequest(ex.Message),
    };
}
