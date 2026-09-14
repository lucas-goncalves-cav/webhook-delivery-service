using WebhookDelivery.Application.Common;

namespace WebhookDelivery.Api.Endpoints;

/// <summary>
/// Translates an application <see cref="Result"/> into an HTTP problem
/// response, so the mapping from error codes to status codes lives in one
/// place rather than in every route.
/// </summary>
public static class ResultExtensions
{
    public static IResult ToProblem(this Result result)
    {
        if (result.IsSuccess)
        {
            throw new InvalidOperationException("A successful result cannot be converted to a problem.");
        }

        return result.Error.Code switch
        {
            "not_found" => Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Resource not found",
                detail: result.Error.Message),

            "conflict" => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Conflict",
                detail: result.Error.Message),

            _ => Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid request",
                detail: result.Error.Message)
        };
    }
}
