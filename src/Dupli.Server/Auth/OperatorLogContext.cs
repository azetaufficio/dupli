using Serilog;
using Serilog.Context;

namespace Dupli.Server.Auth;

/// <summary>Adds who made the request (operator user id and email, or the admin key) to the logs and the request log line.</summary>
public static class OperatorLogContext
{
    public static IApplicationBuilder UseOperatorLogContext(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var user = context.User;
            // Anonymous and agent requests (agent JWT) carry no operator.
            if (!(OperatorAuth.IsApiKey(user) || OperatorAuth.IsBreakGlass(user) || user.HasClaim(c => c.Type == OperatorClaims.ObjectId)))
            {
                await next(context);
                return;
            }

            var userId = user.HasClaim(c => c.Type == OperatorClaims.ObjectId) ? OperatorClaims.UserIdOf(user)?.ToString() : null;
            var actor = OperatorAuth.Actor(user);
            context.RequestServices.GetService<IDiagnosticContext>()?.Set("Operator", actor);
            using (LogContext.PushProperty("OperatorUserId", userId))
            using (LogContext.PushProperty("Operator", actor))
                await next(context);
        });
}
