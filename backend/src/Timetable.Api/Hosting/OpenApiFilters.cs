using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using Timetable.Api.Controllers;
using Timetable.Application.Common;

namespace Timetable.Api.Hosting;

/// <summary>
/// Generic CRUD actions return IActionResult; this filter publishes their real response types (TDto / PagedResult&lt;TDto&gt;)
/// so the generated TypeScript models match the API contract.
/// </summary>
public sealed class CrudResponseOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.ActionDescriptor is not ControllerActionDescriptor action) return;
        var type = action.ControllerTypeInfo.AsType();
        while (type.BaseType is not null && !(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(CrudController<,,>)))
            type = type.BaseType;
        if (!type.IsGenericType) return;
        var dto = type.GetGenericArguments()[1];
        Type? response = action.ActionName switch
        {
            "List" => typeof(PagedResult<>).MakeGenericType(dto),
            "Get" or "Update" => dto,
            "Create" => dto,
            _ => null,
        };
        if (response is null) return;
        var status = action.ActionName == "Create" ? "201" : "200";
        var schema = context.SchemaGenerator.GenerateSchema(response, context.SchemaRepository);
        operation.Responses ??= new OpenApiResponses();
        operation.Responses[status] = new OpenApiResponse
        {
            Description = "Success",
            Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new() { Schema = schema } },
        };
    }
}

/// <summary>Marks non-nullable properties (value types and non-nullable reference types) as required.</summary>
public sealed class RequiredNonNullableSchemaFilter : ISchemaFilter
{
    private static readonly System.Reflection.NullabilityInfoContext Nullability = new();

    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is not OpenApiSchema s || s.Properties is null || context.Type.IsPrimitive || context.Type == typeof(string)) return;
        foreach (var prop in context.Type.GetProperties())
        {
            var name = char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..];
            if (!s.Properties.ContainsKey(name)) continue;
            var t = prop.PropertyType;
            var required = t.IsValueType
                ? Nullable.GetUnderlyingType(t) is null
                : Nullability.Create(prop).ReadState == System.Reflection.NullabilityState.NotNull;
            if (required) (s.Required ??= new HashSet<string>()).Add(name);
        }
    }
}

public static class SchemaIds
{
    /// <summary>Short, stable schema ids: RoomDto, PagedResultOfRoomDto, TemplateBundlePeriodDto (nested).</summary>
    public static string For(Type t)
    {
        if (t.IsGenericType)
        {
            var name = t.Name[..t.Name.IndexOf('`', StringComparison.Ordinal)];
            return name + "Of" + string.Join("And", t.GetGenericArguments().Select(For));
        }
        if (t.IsArray) return For(t.GetElementType()!) + "Array";
        return t.IsNested ? For(t.DeclaringType!) + t.Name : t.Name;
    }
}
