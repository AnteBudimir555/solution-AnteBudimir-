using FluentValidation;
using FluentValidation.Results;
using Middleware.Api.Errors;
using Middleware.Core.Dtos;

namespace Middleware.Api.Validation;

/// <summary>Validated shapes of the product endpoints' request parameters.</summary>
internal sealed record ListRequest(int Page, int Size);

/// <summary>Validated shape of the single-product path variable.</summary>
internal sealed record ProductIdRequest(long Id);

/// <summary>Validated shape of the filter endpoint's parameters (cross-field check lives in the handler).</summary>
internal sealed record FilterRequest(string? Category, decimal? MinPrice, decimal? MaxPrice, int Page, int Size);

/// <summary>Validated shape of the search endpoint's parameters.</summary>
internal sealed record SearchRequest(string? Q, int Page, int Size);

/// <summary>
/// FluentValidation rules mirroring the Java controllers' Bean Validation constraints, message for
/// message. The messages are Hibernate Validator's defaults, because the Java service joins those
/// exact strings into the problem <c>detail</c>; changing them would change the API contract.
///
/// <para>Rule order matches the Java parameter declaration order. Note that this is <em>not</em> the
/// order Java joins multiple violations in: Hibernate Validator returns them from an unordered set,
/// whose iteration order follows no rule the port could reproduce (see the shadowing report). Single
/// violations — the overwhelming majority — match exactly.</para>
/// </summary>
internal static class RequestValidators
{
    /// <summary>
    /// Upper bound on the requested page index. With the max page size of 100 this bounds
    /// <c>page * size</c> well within <c>int</c> range, so the offset passed downstream can never
    /// overflow into a negative value.
    /// </summary>
    public const int MaxPage = 10_000;

    /// <summary>Upper bound on free-text params (search query, category) to reject unbounded input.</summary>
    public const int MaxTextParam = 100;

    private const int MaxSize = 100;

    public static readonly IValidator<ListRequest> List = new ListRequestValidator();
    public static readonly IValidator<ProductIdRequest> ProductId = new ProductIdRequestValidator();
    public static readonly IValidator<FilterRequest> Filter = new FilterRequestValidator();
    public static readonly IValidator<SearchRequest> Search = new SearchRequestValidator();
    public static readonly IValidator<LoginRequest> Login = new LoginRequestValidator();

    private sealed class ListRequestValidator : AbstractValidator<ListRequest>
    {
        public ListRequestValidator()
        {
            RuleFor(r => r.Page).Page();
            RuleFor(r => r.Size).Size();
        }
    }

    private sealed class ProductIdRequestValidator : AbstractValidator<ProductIdRequest>
    {
        public ProductIdRequestValidator() =>
            RuleFor(r => r.Id).GreaterThan(0).WithMessage("must be greater than 0");
    }

    private sealed class FilterRequestValidator : AbstractValidator<FilterRequest>
    {
        public FilterRequestValidator()
        {
            RuleFor(r => r.Category)
                .MaximumLength(MaxTextParam).WithMessage($"size must be between 0 and {MaxTextParam}");
            RuleFor(r => r.MinPrice).PositiveOrZero();
            RuleFor(r => r.MaxPrice).PositiveOrZero();
            RuleFor(r => r.Page).Page();
            RuleFor(r => r.Size).Size();
        }
    }

    private sealed class SearchRequestValidator : AbstractValidator<SearchRequest>
    {
        public SearchRequestValidator()
        {
            RuleFor(r => r.Q)
                .Must(q => !string.IsNullOrWhiteSpace(q)).WithMessage("must not be blank")
                .MaximumLength(MaxTextParam).WithMessage($"size must be between 0 and {MaxTextParam}");
            RuleFor(r => r.Page).Page();
            RuleFor(r => r.Size).Size();
        }
    }

    private sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
    {
        public LoginRequestValidator()
        {
            RuleFor(r => r.Username).Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("must not be blank");
            RuleFor(r => r.Password).Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("must not be blank");
        }
    }

    private static IRuleBuilderOptions<T, int> Page<T>(this IRuleBuilder<T, int> rule) =>
        rule.GreaterThanOrEqualTo(0).WithMessage("must be greater than or equal to 0")
            .LessThanOrEqualTo(MaxPage).WithMessage($"must be less than or equal to {MaxPage}");

    private static IRuleBuilderOptions<T, int> Size<T>(this IRuleBuilder<T, int> rule) =>
        rule.GreaterThanOrEqualTo(1).WithMessage("must be greater than or equal to 1")
            .LessThanOrEqualTo(MaxSize).WithMessage($"must be less than or equal to {MaxSize}");

    private static IRuleBuilderOptions<T, decimal?> PositiveOrZero<T>(this IRuleBuilder<T, decimal?> rule) =>
        rule.GreaterThanOrEqualTo(0m).WithMessage("must be greater than or equal to 0");
}

/// <summary>
/// Runs a validator and converts failures into the API's validation problem.
///
/// <para>Two joins exist because the Java service produces two. Parameter-level violations reach
/// Spring as a <c>ConstraintViolationException</c> whose property path is
/// <c>&lt;controllerMethod&gt;.&lt;parameter&gt;</c>, so each message is prefixed with that path —
/// hence the <c>scope</c> argument, which names the Java controller method the endpoint was ported
/// from. Request-body violations (<c>MethodArgumentNotValidException</c>) are prefixed with the field
/// name alone.</para>
/// </summary>
internal static class RequestValidation
{
    /// <summary>Validates query/route parameters; joins <c>scope.parameter: message</c> triples.</summary>
    public static void EnsureValidParameters<T>(IValidator<T> validator, T instance, string scope)
    {
        var result = validator.Validate(instance);
        if (!result.IsValid)
        {
            throw new RequestValidationException(
                Join(result, error => $"{scope}.{Camel(error.PropertyName)}: {error.ErrorMessage}"));
        }
    }

    /// <summary>Validates a request body; joins <c>field: message</c> pairs.</summary>
    public static void EnsureValidBody<T>(IValidator<T> validator, T instance)
    {
        var result = validator.Validate(instance);
        if (!result.IsValid)
        {
            throw new RequestValidationException(Join(result, error => $"{Camel(error.PropertyName)}: {error.ErrorMessage}"));
        }
    }

    private static string Join(ValidationResult result, Func<ValidationFailure, string> render) =>
        string.Join("; ", result.Errors.Select(render));

    /// <summary>C# properties are PascalCase; the JSON/API field names they report are camelCase.</summary>
    private static string Camel(string propertyName) =>
        string.IsNullOrEmpty(propertyName) ? propertyName : char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
}
