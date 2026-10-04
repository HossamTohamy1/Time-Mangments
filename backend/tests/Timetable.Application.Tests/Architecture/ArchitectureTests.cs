using NetArchTest.Rules;

namespace Timetable.Application.Tests.Architecture;

/// <summary>Enforces the Clean Architecture dependency rule.</summary>
public sealed class ArchitectureTests
{
    private const string Domain = "Timetable.Domain";
    private const string Application = "Timetable.Application";
    private const string Infrastructure = "Timetable.Infrastructure";
    private const string Api = "Timetable.Api";

    [Fact]
    public void Domain_depends_on_nothing()
    {
        var r = Types.InAssembly(typeof(Domain.Common.Entity).Assembly).ShouldNot()
            .HaveDependencyOnAny(Application, Infrastructure, Api, "Microsoft.EntityFrameworkCore", "MediatR", "Microsoft.AspNetCore").GetResult();
        r.IsSuccessful.ShouldBeTrue(string.Join(", ", r.FailingTypeNames ?? []));
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure_or_api()
    {
        var r = Types.InAssembly(typeof(Application.DependencyInjection).Assembly).ShouldNot()
            .HaveDependencyOnAny(Infrastructure, Api, "Microsoft.AspNetCore").GetResult();
        r.IsSuccessful.ShouldBeTrue(string.Join(", ", r.FailingTypeNames ?? []));
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_api()
    {
        var r = Types.InAssembly(typeof(Infrastructure.DependencyInjection).Assembly).ShouldNot().HaveDependencyOn(Api).GetResult();
        r.IsSuccessful.ShouldBeTrue(string.Join(", ", r.FailingTypeNames ?? []));
    }

    [Fact]
    public void Domain_has_no_business_concept_enums()
    {
        // Business "types" are data. Only technical enums are allowed in the domain.
        var allowed = new HashSet<string>
        {
            "ScheduleStatus", "ConstraintSeverity", "AvailabilityState", "CustomFieldDataType", "CustomFieldEntity",
            "ScheduleExceptionKind", "SubstitutionStatus", "CalendarDayKind", "GenerationJobStatus", "ChangeAction",
            "ErrorKind", "ResourceKind", "ViolationSeverity", "EntityRefKind", "ConstraintArity", "ParameterType",
            "AvailabilityStateValue", "SlotStatus", "RuleEffect",
        };
        var enums = typeof(Domain.Common.Entity).Assembly.GetTypes().Where(t => t.IsEnum).Select(t => t.Name).ToList();
        enums.Where(e => !allowed.Contains(e)).ShouldBeEmpty();
        var forbidden = new[] { "University", "School", "Doctor", "Lecture", "Faculty", "Grade", "Teacher", "Section", "Lab" };
        foreach (var e in typeof(Domain.Common.Entity).Assembly.GetTypes().Where(t => t.IsEnum))
            foreach (var name in Enum.GetNames(e))
                forbidden.ShouldNotContain(name, $"{e.Name}.{name} looks like a hard-coded business concept");
    }
}
