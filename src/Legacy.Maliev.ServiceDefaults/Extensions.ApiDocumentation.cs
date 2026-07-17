using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Scalar.AspNetCore;
using System.Globalization;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for configuring API documentation (OpenAPI + Scalar) in the application.
/// </summary>
public static class ApiDocumentationExtensions
{
    /// <summary>
    /// Maps Scalar API documentation and OpenAPI endpoints to the application.
    /// Only available in non-production environments.
    /// </summary>
    /// <param name="app">The web application.</param>
    /// <param name="servicePrefix">Optional URL prefix for the service (e.g., "auth" for /auth/scalar).</param>
    /// <param name="documentName">The OpenAPI document name (default: "v1").</param>
    /// <param name="configureScalar">Optional action to configure Scalar options.</param>
    /// <returns>The configured application.</returns>
    public static WebApplication MapApiDocumentation(
        this WebApplication app,
        string? servicePrefix = null,
        string documentName = "v1",
        Action<ScalarOptions>? configureScalar = null)
    {
        if (app.Environment.IsProduction())
        {
            return app;
        }

        var urlPrefix = string.IsNullOrEmpty(servicePrefix) ? "" : $"/{servicePrefix}";
        var openApiRoute = $"{urlPrefix}/openapi/{documentName}.json";
        var scalarRoute = $"{urlPrefix}/scalar";

        app.MapOpenApi(openApiRoute);

        app.MapScalarApiReference(scalarRoute, opt =>
        {
            TextInfo documentationTitle = new CultureInfo("en-US").TextInfo;
            var displayTitle = string.IsNullOrEmpty(servicePrefix) ? "API" : servicePrefix.Replace("-", " ");
            opt.Title = $"{documentationTitle.ToTitleCase(displayTitle)} Documentation";
            opt.OpenApiRoutePattern = openApiRoute;
            configureScalar?.Invoke(opt);
        });

        return app;
    }

    /// <summary>
    /// Adds standard OpenAPI document generation with optional title and description.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="title">Optional API title.</param>
    /// <param name="description">Optional API description.</param>
    /// <param name="documentName">The OpenAPI document name (default: "v1").</param>
    /// <returns>The configured builder.</returns>
    public static IHostApplicationBuilder AddStandardOpenApi(
        this IHostApplicationBuilder builder,
        string? title = null,
        string? description = null,
        string documentName = "v1")
    {
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddOpenApi(documentName, options =>
        {
            if (title != null || description != null)
            {
                options.AddDocumentTransformer((document, context, cancellationToken) =>
                {
                    if (title != null) document.Info.Title = title;
                    if (description != null) document.Info.Description = description;
                    return Task.CompletedTask;
                });
            }
        });

        return builder;
    }

    /// <summary>
    /// Maps Scalar API documentation with default configuration (no service prefix).
    /// Only available in Development environment.
    /// </summary>
    /// <param name="app">The web application.</param>
    /// <param name="documentName">The OpenAPI document name (default: "v1").</param>
    /// <param name="configureScalar">Optional action to configure Scalar options.</param>
    /// <returns>The configured application.</returns>
    public static WebApplication MapApiDocumentationDefault(
        this WebApplication app,
        string documentName = "v1",
        Action<ScalarOptions>? configureScalar = null)
    {
        return app.MapApiDocumentation(servicePrefix: null, documentName: documentName, configureScalar: configureScalar);
    }

    /// <summary>
    /// Adds Scalar API documentation (NOT Swagger).
    /// Configures OpenAPI with the specified title and version.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="apiTitle">The API title.</param>
    /// <param name="apiVersion">The API version (default: "v1").</param>
    /// <returns>The configured builder.</returns>
    public static IHostApplicationBuilder AddScalarApiDocumentation(
        this IHostApplicationBuilder builder,
        string apiTitle,
        string apiVersion = "v1")
    {
        return builder.AddStandardOpenApi(title: apiTitle, documentName: apiVersion);
    }

    /// <summary>
    /// Maps Scalar API documentation UI endpoint.
    /// Only available in Development environment.
    /// </summary>
    /// <param name="app">The web application.</param>
    /// <returns>The configured application.</returns>
    public static WebApplication UseScalarApiDocumentation(
        this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapApiDocumentationDefault();
        }

        return app;
    }
}
