using System.Reflection;

namespace MediStock.Domain.Tests;

/// <summary>
/// Stands in for the identity Entity Framework assigns when an entity is saved.
/// </summary>
/// <remarks>
/// Documents reference each other by key — a return line names the invoice line it credits, and the
/// domain refuses a reference it cannot resolve — so a unit test exercising that document has to give
/// its entities the keys that saving would have produced, without going near a database.
/// </remarks>
internal static class TestIdentity
{
    internal static T WithId<T>(this T entity, int id)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);

        var property = typeof(T).GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{typeof(T).Name} has no Id property to stand in for.");

        property.SetValue(entity, id);
        return entity;
    }
}
