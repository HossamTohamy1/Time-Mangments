using FluentValidation;

namespace Timetable.Application.Common.Crud;

public interface IBilingualInput
{
    string Code { get; }
    string? NameAr { get; }
    string? NameEn { get; }
}

public static class BilingualValidation
{
    public const string CodePattern = "^[A-Za-z0-9][A-Za-z0-9_.\\-]{0,63}$";

    /// <summary>Code format + at least one of the two names (both ≤ 200 chars).</summary>
    public static void AddBilingualRules<T>(this AbstractValidator<T> v) where T : IBilingualInput
    {
        v.RuleFor(x => x.Code).NotEmpty().WithErrorCode("FIELD_REQUIRED").Matches(CodePattern).WithErrorCode("CODE_INVALID");
        v.RuleFor(x => x.NameEn).Must((x, _) => !string.IsNullOrWhiteSpace(x.NameAr) || !string.IsNullOrWhiteSpace(x.NameEn))
            .WithErrorCode("NAME_REQUIRED").WithName("name");
        v.RuleFor(x => x.NameAr).MaximumLength(200).WithErrorCode("VALUE_OUT_OF_RANGE");
        v.RuleFor(x => x.NameEn).MaximumLength(200).WithErrorCode("VALUE_OUT_OF_RANGE");
    }
}
