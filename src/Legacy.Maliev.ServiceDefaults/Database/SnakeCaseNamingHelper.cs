using Microsoft.EntityFrameworkCore;

namespace Maliev.Aspire.ServiceDefaults.Database;

/// <summary>
/// Helper to apply snake_case naming convention to all database entities.
/// This follows PostgreSQL naming conventions.
/// </summary>
public static class SnakeCaseNamingHelper
{
    /// <summary>
    /// Applies snake_case naming convention to all entity types in the model.
    /// Call this from your DbContext's OnModelCreating method.
    /// </summary>
    /// <param name="modelBuilder">The ModelBuilder instance</param>
    public static void ApplySnakeCaseNaming(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            // Convert table names to snake_case
            var tableName = entity.GetTableName();
            if (!string.IsNullOrEmpty(tableName))
            {
                entity.SetTableName(ToSnakeCase(tableName));
            }

            // Convert column names to snake_case
            foreach (var property in entity.GetProperties())
            {
                var columnName = property.GetColumnName();
                if (!string.IsNullOrEmpty(columnName))
                {
                    property.SetColumnName(ToSnakeCase(columnName));
                }
            }

            // Convert primary/foreign key names to snake_case
            foreach (var key in entity.GetKeys())
            {
                var keyName = key.GetName();
                if (!string.IsNullOrEmpty(keyName))
                {
                    key.SetName(ToSnakeCase(keyName));
                }
            }

            // Convert foreign key constraint names to snake_case
            foreach (var fk in entity.GetForeignKeys())
            {
                var fkName = fk.GetConstraintName();
                if (!string.IsNullOrEmpty(fkName))
                {
                    fk.SetConstraintName(ToSnakeCase(fkName));
                }
            }

            // Convert index names to snake_case
            foreach (var index in entity.GetIndexes())
            {
                var indexName = index.GetDatabaseName();
                if (!string.IsNullOrEmpty(indexName))
                {
                    index.SetDatabaseName(ToSnakeCase(indexName));
                }
            }
        }
    }

    /// <summary>
    /// Converts a string to snake_case following these rules:
    /// - PascalCase -> pascal_case
    /// - camelCase -> camel_case
    /// - Handles acronyms: HTTPSConnection -> https_connection
    /// </summary>
    /// <param name="input">The string to convert</param>
    /// <returns>The snake_case version of the input</returns>
    public static string ToSnakeCase(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var result = new System.Text.StringBuilder();
        result.Append(char.ToLowerInvariant(input[0]));

        for (int i = 1; i < input.Length; i++)
        {
            char c = input[i];
            if (char.IsUpper(c))
            {
                // Add underscore before uppercase letter if:
                // 1. Previous char is lowercase, OR
                // 2. Next char exists and is lowercase (handles acronyms like "HTTPSConnection" -> "https_connection")
                if (char.IsLower(input[i - 1]) || (i < input.Length - 1 && char.IsLower(input[i + 1])))
                {
                    result.Append('_');
                }
                result.Append(char.ToLowerInvariant(c));
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }
}
