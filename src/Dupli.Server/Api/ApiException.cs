namespace Dupli.Server.Api;

/// <summary>Expected failure mapped to a ProblemDetails response with <see cref="StatusCode"/>.</summary>
public sealed class ApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public static ApiException NotFound(string what) => new(StatusCodes.Status404NotFound, $"{what} not found");
    public static ApiException BadRequest(string message) => new(StatusCodes.Status400BadRequest, message);
    public static ApiException Conflict(string message) => new(StatusCodes.Status409Conflict, message);
    public static ApiException Unauthorized(string message) => new(StatusCodes.Status401Unauthorized, message);
}

public static class ApiExceptionMiddleware
{
    public static IApplicationBuilder UseApiExceptions(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (ApiException ex) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = ex.StatusCode;
                await Results.Problem(statusCode: ex.StatusCode, detail: ex.Message).ExecuteAsync(context);
            }
        });
}
