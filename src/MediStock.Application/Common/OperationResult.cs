namespace MediStock.Application.Common;

/// <summary>
/// Outcome of an application operation. Failures carry messages that are already safe to show to
/// an operator, so view models never have to inspect exception text.
/// </summary>
public sealed class OperationResult
{
    private static readonly IReadOnlyList<string> NoErrors = Array.Empty<string>();

    private OperationResult(bool succeeded, IReadOnlyList<string> errors)
    {
        Succeeded = succeeded;
        Errors = errors;
    }

    public bool Succeeded { get; }

    public IReadOnlyList<string> Errors { get; }

    public string Message => string.Join(Environment.NewLine, Errors);

    public static OperationResult Success() => new(true, NoErrors);

    public static OperationResult Fail(params string[] errors) => Fail((IEnumerable<string>)errors);

    public static OperationResult Fail(IEnumerable<string> errors)
    {
        var list = errors.Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
        return new OperationResult(false, list.Count == 0 ? ["The operation failed."] : list);
    }

    public static OperationResult From(Exception exception) => Fail(Describe(exception));

    internal static string Describe(Exception exception) => exception switch
    {
        Domain.Common.DomainException => exception.Message,
        _ => "An unexpected error occurred. Check the application log for details.",
    };
}

/// <summary>Outcome of an application operation that produces a value.</summary>
/// <typeparam name="T">Type of the produced value.</typeparam>
public sealed class OperationResult<T>
{
    private OperationResult(bool succeeded, T? value, IReadOnlyList<string> errors)
    {
        Succeeded = succeeded;
        Value = value;
        Errors = errors;
    }

    public bool Succeeded { get; }

    public T? Value { get; }

    public IReadOnlyList<string> Errors { get; }

    public string Message => string.Join(Environment.NewLine, Errors);

    public static OperationResult<T> Success(T value) => new(true, value, Array.Empty<string>());

    public static OperationResult<T> Fail(params string[] errors) => Fail((IEnumerable<string>)errors);

    public static OperationResult<T> Fail(IEnumerable<string> errors)
    {
        var list = errors.Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
        return new OperationResult<T>(false, default, list.Count == 0 ? ["The operation failed."] : list);
    }

    public static OperationResult<T> From(Exception exception)
        => Fail(OperationResult.Describe(exception));
}
